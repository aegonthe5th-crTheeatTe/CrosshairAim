// CrosshairAim - Plugin Loader plugin for Space Engineers 1 (single player).
//
// Makes bullets and "use" interactions go where the on-screen crosshair points while a camera mod
// (Over The Shoulder+, the cover mod) is driving the camera.
//
// Why it is needed: when a mod takes over the camera the game no longer treats the character as
// "aiming from the camera". Weapons then fire at a point 10 m in front of the head, and the use
// ray starts at the head, so neither agrees with a camera that sits beside the head.
//
// What it does (only while a mod says its camera is active, otherwise the game is untouched):
//   1. MyCharacter.GetAimedPointFromCamera is replaced by the same calculation the game does in its
//      own third person, but using the camera that is really on screen.
//   2. The two use-detection methods (MyCharacterShapecastDetectorComponent / ...Raycast...) get
//      their ray start and direction replaced by the on-screen camera ray (moved to head depth).
//
// Everything is looked up by name through reflection, so a game update that renames something
// switches that one patch off and writes a line to the game log instead of crashing.
//
// Mods tell the plugin they are active with SendModMessage(id, true) about every 10 ticks, and
// false when done. The plugin treats a mod as inactive 750 ms after its last "true".

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace CrosshairAim
{
  public class Plugin : IPlugin
  {
    private const long OtsMessageId = 4207130002L;
    private const long CoverMessageId = 4207130003L;
    private const long ZoomMessageId = 4207130004L;   // float: field of view as a fraction of normal (from OTS+)
    private const long WalkMessageId = 4207130007L;     // float from the cover mod: -1 none, 0 block walking and jumping, 1 walk forward, 2 block jumping only
    private const long HideCrosshairMessageId = 4207130008L; // bool from the cover mod: hide the game's crosshair (a red one is drawn instead)
    private const long SprintMessageId = 4207130006L; // bool: the cover mod is running you to a marked wall, hold sprint
    private const double TimeoutMs = 750.0;
    private const string Tag = "[CrosshairAim] ";

    private static Harmony _harmony;
    private static IMyUtilities _registeredTo;
    private static DateTime _otsSeen = DateTime.MinValue, _coverSeen = DateTime.MinValue;
    private static bool _otsOn, _coverOn;
    private static bool _aimBroken;
    private static float _zoomWanted = 1f, _zoomCurrent = 1f, _baseFov;
    private static DateTime _zoomSeen = DateTime.MinValue;
    private static MemberInfo _fov;               // MyCamera.FieldOfViewDegrees
    private static DateTime _lastStatusLog = DateTime.MinValue;

    // reflection handles (all filled in Init)
    private static Assembly _game;
    private static bool _aimSeen, _useSeen, _sprintSeen;
    private static bool _sprintOn;
    private static float _walkMode = -1f;
    private static DateTime _walkStamp = DateTime.MinValue;
    private static bool _walkSeen, _jumpSeen, _crossSeen;
    private static bool _hideCross;
    private static DateTime _hideStamp = DateTime.MinValue;
    private static DateTime _sprintStamp = DateTime.MinValue;
    private static MethodInfo _sprintMethod;      // MyCharacter.Sprint(bool)
    private static MethodInfo _getHead;           // MyCharacter.GetHeadMatrix(bool...)
    private static int _getHeadArgs;
    private static MethodInfo _getWeaponPos;      // MyCharacter.get_WeaponPosition
    private static PropertyInfo _logicalPos;      // MyCharacterWeaponPositionComponent.LogicalPositionWorld
    private static MethodInfo _getCurrentWeapon;  // MyCharacter.get_CurrentWeapon
    private static PropertyInfo _sessionStatic;   // MySession.Static
    private static PropertyInfo _localCharacter;  // MySession.LocalCharacter
    private static MemberInfo _mainCamera;        // MySector.MainCamera
    private static MemberInfo _camWorldMatrix;    // MyCamera.WorldMatrix
    private static MethodInfo _castRay;           // MyPhysics.CastRay(Vector3D, Vector3D, int)
    private static MemberInfo _hitPosition;       // HitInfo.Position

    public void Init(object gameInstance)
    {
      try
      {
        _game = gameInstance != null ? gameInstance.GetType().Assembly : null;
        Log("starting (init object: " + (gameInstance != null ? gameInstance.GetType().FullName : "null") + ")");
        Resolve();
        _harmony = new Harmony("crosshairaim.plugin");
        PatchAim();
        PatchUse("Sandbox.Game.Entities.Character.Components.MyCharacterShapecastDetectorComponent",
                 "DoDetection", ShapeTranspiler);
        PatchUse("Sandbox.Game.Entities.Character.Components.MyCharacterRaycastDetectorComponent",
                 "DoDetection", RayTranspiler);
        PatchSprint();
        PatchWalk();
        PatchJump();
        PatchCrosshair();
      }
      catch (Exception e)
      {
        Log("init failed: " + e);
      }
    }

    public void Update()
    {
      // Register for the mods' messages whenever a (new) session's utilities object appears.
      try
      {
        IMyUtilities u = MyAPIGateway.Utilities;
        if (u != _registeredTo)
        {
          _registeredTo = u;
          _otsOn = _coverOn = false;
          if (u != null)
          {
            u.RegisterMessageHandler(OtsMessageId, OnOts);
            u.RegisterMessageHandler(CoverMessageId, OnCover);
            u.RegisterMessageHandler(ZoomMessageId, OnZoom);
            u.RegisterMessageHandler(SprintMessageId, OnSprint);
            u.RegisterMessageHandler(WalkMessageId, OnWalk);
            u.RegisterMessageHandler(HideCrosshairMessageId, OnHideCrosshair);
            Log("listening for camera mods");
          }
        }
      }
      catch (Exception e)
      {
        Log("message registration failed: " + e.Message);
      }

      UpdateZoom();
      UpdateSprint();
    }

    // ------------------------------------------------------------------ sprint for the cover mod's run
    private static void OnSprint(object m)
    {
      try
      {
        _sprintOn = m is bool && (bool)m;
        _sprintStamp = DateTime.UtcNow;
      }
      catch (Exception) { }
    }

    private static bool SprintHeld
    {
      get { return _sprintOn && (DateTime.UtcNow - _sprintStamp).TotalMilliseconds < TimeoutMs; }
    }

    // While the cover mod runs you to a marked wall, keep the character sprinting.
    private static void UpdateSprint()
    {
      if (_sprintMethod == null || !SprintHeld || _sessionStatic == null || _localCharacter == null)
        return;
      try
      {
        object s = _sessionStatic.GetValue(null, null);
        object ch = s == null ? null : _localCharacter.GetValue(s, null);
        if (ch == null) return;
        _sprintMethod.Invoke(ch, new object[] { true });
        if (!_sprintSeen) { _sprintSeen = true; Log("sprint held for the first time (cover run)"); }
      }
      catch (Exception e)
      {
        _sprintMethod = null;
        Log("sprint switched off after an error: " + e.Message);
      }
    }

    // ------------------------------------------------------------------ walking along cover for the cover mod
    private static void OnWalk(object m)
    {
      try
      {
        _walkMode = Convert.ToSingle(m);
        _walkStamp = DateTime.UtcNow;
      }
      catch (Exception) { }
    }

    // Harmony prefix on MyCharacter.Jump: while the cover mod says you are in cover, the game's jump never starts.
    // (The cover mod's own hop over low cover is scripted and does not use it.)
    public static bool JumpPrefix(object __instance)
    {
      try
      {
        if (_walkMode < 0f || (DateTime.UtcNow - _walkStamp).TotalMilliseconds >= TimeoutMs)
          return true;
        if (!IsLocal(__instance))
          return true;
        if (!_jumpSeen) { _jumpSeen = true; Log("jump blocked in cover for the first time"); }
        return false;
      }
      catch (Exception) { }
      return true;
    }

    // ------------------------------------------------------------------ hide the game's crosshair for the cover mod's red one
    private static void OnHideCrosshair(object m)
    {
      try
      {
        _hideCross = AsBool(m);
        _hideStamp = DateTime.UtcNow;
      }
      catch (Exception) { }
    }

    // Harmony prefix on MyHudCrosshair.Draw: skipped while the cover mod asks for it. The player's crosshair setting is never touched.
    public static bool CrosshairPrefix()
    {
      try
      {
        if (_hideCross && (DateTime.UtcNow - _hideStamp).TotalMilliseconds < TimeoutMs)
        {
          if (!_crossSeen) { _crossSeen = true; Log("game crosshair hidden for the first time (hostile under it)"); }
          return false;
        }
      }
      catch (Exception) { }
      return true;
    }

    // Harmony prefix on MyCharacter.MoveAndRotate: while the cover mod asks for it, the player's own move keys are
    // replaced (no sideways step, forward only when asked, no jump). The mouse turning (the second argument) is untouched.
    public static bool WalkPrefix(object __instance, ref Vector3 __0)
    {
      try
      {
        if (_walkMode < 0f || _walkMode > 1.5f || (DateTime.UtcNow - _walkStamp).TotalMilliseconds >= TimeoutMs)
          return true;
        if (!IsLocal(__instance))
          return true;
        __0 = new Vector3(0f, 0f, _walkMode > 0.5f ? -1f : 0f);
        if (!_walkSeen) { _walkSeen = true; Log("walk override active for the first time (cover A/D)"); }
      }
      catch (Exception) { }
      return true;
    }

    // Harmony prefix: while the run is on, the game's own "stop sprinting" calls are turned back into "sprint".
    public static bool SprintPrefix(ref bool __0)
    {
      if (SprintHeld)
        __0 = true;
      return true;
    }

    // Field of view while aiming: eases toward the fraction the mod asks for, and back to normal afterwards.
    private static void UpdateZoom()
    {
      if (_fov == null || _mainCamera == null)
        return;
      try
      {
        float target = (Active && (DateTime.UtcNow - _zoomSeen).TotalMilliseconds < TimeoutMs) ? _zoomWanted : 1f;
        if (Math.Abs(_zoomCurrent - 1f) < 0.001f && Math.Abs(target - 1f) < 0.001f)
          return;

        object cam = GetValue(_mainCamera, null);
        if (cam == null) return;

        if (Math.Abs(_zoomCurrent - 1f) < 0.001f)
          _baseFov = Convert.ToSingle(GetValue(_fov, cam));   // remember the player's own setting

        _zoomCurrent += (target - _zoomCurrent) * 0.15f;
        if (Math.Abs(_zoomCurrent - target) < 0.002f)
          _zoomCurrent = target;

        if (_baseFov > 1f)
          SetValue(_fov, cam, _baseFov * _zoomCurrent);
      }
      catch (Exception e)
      {
        _fov = null;
        Log("zoom switched off after an error: " + e.Message);
      }
    }

    private static void OnZoom(object m)
    {
      try
      {
        _zoomWanted = Math.Max(0.3f, Math.Min(1f, Convert.ToSingle(m)));
        _zoomSeen = DateTime.UtcNow;
      }
      catch (Exception) { }
    }

    public void Dispose()
    {
      try
      {
        if (_registeredTo != null)
        {
          _registeredTo.UnregisterMessageHandler(OtsMessageId, OnOts);
          _registeredTo.UnregisterMessageHandler(CoverMessageId, OnCover);
          _registeredTo.UnregisterMessageHandler(ZoomMessageId, OnZoom);
          _registeredTo.UnregisterMessageHandler(SprintMessageId, OnSprint);
          _registeredTo.UnregisterMessageHandler(WalkMessageId, OnWalk);
          _registeredTo.UnregisterMessageHandler(HideCrosshairMessageId, OnHideCrosshair);
        }
        // Harmony patches are left in place on purpose: unpatching can disturb other plugins, and the game is closing.
      }
      catch (Exception) { }
    }

    // ------------------------------------------------------------------ mod messages

    private static bool AsBool(object o)
    {
      if (o is bool) return (bool)o;
      if (o is int) return (int)o != 0;
      if (o is long) return (long)o != 0;
      return false;
    }

    private static void OnOts(object m)
    {
      bool on = AsBool(m);
      if (on != _otsOn) Log("Over The Shoulder+ camera " + (on ? "active: aim and use follow the crosshair" : "inactive"));
      _otsOn = on;
      if (on) _otsSeen = DateTime.UtcNow;
    }

    private static void OnCover(object m)
    {
      bool on = AsBool(m);
      if (on != _coverOn) Log("cover camera " + (on ? "active: aim and use follow the crosshair" : "inactive"));
      _coverOn = on;
      if (on) _coverSeen = DateTime.UtcNow;
    }

    public static bool Active
    {
      get
      {
        DateTime now = DateTime.UtcNow;
        return (_otsOn && (now - _otsSeen).TotalMilliseconds < TimeoutMs)
            || (_coverOn && (now - _coverSeen).TotalMilliseconds < TimeoutMs);
      }
    }

    // ------------------------------------------------------------------ reflection setup

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

    private static Type T(string name)
    {
      Type t = null;
      try { if (_game != null) t = _game.GetType(name, false); } catch (Exception) { }
      if (t == null)
      {
        // The object Pulsar passes to Init is not always from the game's own assembly, so search everything loaded.
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
          try { t = a.GetType(name, false); } catch (Exception) { }
          if (t != null) break;
        }
      }
      if (t == null) Log("type not found: " + name);
      return t;
    }

    private static void Resolve()
    {
      Type character = T("Sandbox.Game.Entities.Character.MyCharacter");
      Type session = T("Sandbox.Game.World.MySession");
      Type sector = T("Sandbox.Game.World.MySector");
      Type physics = T("Sandbox.Engine.Physics.MyPhysics");

      if (character != null)
      {
        foreach (MethodInfo m in character.GetMethods(All))
        {
          if (m.Name != "GetHeadMatrix") continue;
          ParameterInfo[] ps = m.GetParameters();
          bool allBool = true;
          foreach (ParameterInfo p in ps) if (p.ParameterType != typeof(bool)) allBool = false;
          if (allBool && ps.Length >= 4 && (_getHead == null || ps.Length > _getHeadArgs))
          {
            _getHead = m;
            _getHeadArgs = ps.Length;
          }
        }
        _getWeaponPos = PropGetter(character, "WeaponPosition");
        _getCurrentWeapon = PropGetter(character, "CurrentWeapon");
        if (_getWeaponPos != null)
          _logicalPos = _getWeaponPos.ReturnType.GetProperty("LogicalPositionWorld", All);
      }
      if (session != null)
      {
        _sessionStatic = session.GetProperty("Static", All);
        _localCharacter = session.GetProperty("LocalCharacter", All);
      }
      if (sector != null)
      {
        _mainCamera = (MemberInfo)sector.GetProperty("MainCamera", All) ?? sector.GetField("MainCamera", All);
        Type camType = MemberType(_mainCamera);
        if (camType != null)
        {
          _camWorldMatrix = (MemberInfo)camType.GetField("WorldMatrix", All) ?? camType.GetProperty("WorldMatrix", All);
          _fov = (MemberInfo)camType.GetProperty("FieldOfViewDegrees", All) ?? camType.GetField("FieldOfViewDegrees", All);
        }
      }
      if (physics != null)
      {
        foreach (MethodInfo m in physics.GetMethods(All))
        {
          if (m.Name != "CastRay" || !m.IsStatic) continue;
          ParameterInfo[] ps = m.GetParameters();
          if (ps.Length == 3 && ps[0].ParameterType == typeof(Vector3D) &&
              ps[1].ParameterType == typeof(Vector3D) && ps[2].ParameterType == typeof(int))
            _castRay = m;
        }
        if (_castRay != null)
        {
          Type hit = Nullable.GetUnderlyingType(_castRay.ReturnType) ?? _castRay.ReturnType;
          _hitPosition = (MemberInfo)hit.GetField("Position", All) ?? hit.GetProperty("Position", All);
        }
      }

      Log("resolved: head=" + (_getHead != null) + " weaponPos=" + (_getWeaponPos != null) +
          " weapon=" + (_getCurrentWeapon != null) + " session=" + (_localCharacter != null) +
          " camera=" + (_camWorldMatrix != null) + " zoom=" + (_fov != null) + " castRay=" + (_castRay != null && _hitPosition != null));
    }

    private static MethodInfo PropGetter(Type t, string name)
    {
      PropertyInfo p = t.GetProperty(name, All);
      return p == null ? null : p.GetGetMethod(true);
    }

    private static Type MemberType(MemberInfo m)
    {
      PropertyInfo p = m as PropertyInfo;
      if (p != null) return p.PropertyType;
      FieldInfo f = m as FieldInfo;
      return f != null ? f.FieldType : null;
    }

    private static void SetValue(MemberInfo m, object target, object value)
    {
      PropertyInfo p = m as PropertyInfo;
      if (p != null) { p.SetValue(target, value, null); return; }
      ((FieldInfo)m).SetValue(target, value);
    }

    private static object GetValue(MemberInfo m, object target)
    {
      PropertyInfo p = m as PropertyInfo;
      if (p != null) return p.GetValue(target, null);
      return ((FieldInfo)m).GetValue(target);
    }

    // ------------------------------------------------------------------ patch setup

    private static void PatchAim()
    {
      Type character = T("Sandbox.Game.Entities.Character.MyCharacter");
      MethodInfo target = character == null ? null : character.GetMethod("GetAimedPointFromCamera", All);
      if (target == null || _getHead == null || _camWorldMatrix == null || _localCharacter == null)
      {
        Log("aim patch skipped (something was not found)");
        return;
      }
      DoPatch(target, new HarmonyMethod(typeof(Plugin).GetMethod("AimPrefix", BindingFlags.Static | BindingFlags.Public)), null);
      Log("aim patch applied");
    }

    private static void PatchSprint()
    {
      try
      {
        Type character = T("Sandbox.Game.Entities.Character.MyCharacter");
        if (character == null) { Log("sprint patch skipped (character type not found)"); return; }
        foreach (MethodInfo m in character.GetMethods(All | BindingFlags.DeclaredOnly))
        {
          if (m.Name != "Sprint") continue;
          ParameterInfo[] ps = m.GetParameters();
          if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { _sprintMethod = m; break; }
        }
        if (_sprintMethod == null) { Log("sprint patch skipped (Sprint(bool) not found)"); return; }
        DoPatch(_sprintMethod, new HarmonyMethod(typeof(Plugin).GetMethod("SprintPrefix", BindingFlags.Static | BindingFlags.Public)), null);
        Log("sprint patch applied");
      }
      catch (Exception e)
      {
        _sprintMethod = null;
        Log("sprint patch failed: " + e.Message);
      }
    }

    private static void PatchJump()
    {
      try
      {
        Type character = T("Sandbox.Game.Entities.Character.MyCharacter");
        if (character == null) { Log("jump patch skipped (character type not found)"); return; }
        MethodInfo target = null;
        foreach (MethodInfo m in character.GetMethods(All | BindingFlags.DeclaredOnly))
        {
          if (m.Name != "Jump") continue;
          ParameterInfo[] ps = m.GetParameters();
          if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3)) { target = m; break; }
        }
        if (target == null) { Log("jump patch skipped (Jump(Vector3) not found)"); return; }
        DoPatch(target, new HarmonyMethod(typeof(Plugin).GetMethod("JumpPrefix", BindingFlags.Static | BindingFlags.Public)), null);
        Log("jump patch applied");
      }
      catch (Exception e)
      {
        Log("jump patch failed: " + e.Message);
      }
    }

    private static void PatchCrosshair()
    {
      try
      {
        Type crosshair = T("Sandbox.Game.Gui.MyHudCrosshair");
        if (crosshair == null) { Log("crosshair patch skipped (type not found)"); return; }
        MethodInfo target = null;
        foreach (MethodInfo m in crosshair.GetMethods(All | BindingFlags.DeclaredOnly))
        {
          if (m.Name == "Draw") { target = m; break; }
        }
        if (target == null) { Log("crosshair patch skipped (Draw not found)"); return; }
        DoPatch(target, new HarmonyMethod(typeof(Plugin).GetMethod("CrosshairPrefix", BindingFlags.Static | BindingFlags.Public)), null);
        Log("crosshair patch applied");
      }
      catch (Exception e)
      {
        Log("crosshair patch failed: " + e.Message);
      }
    }

    private static void PatchWalk()
    {
      try
      {
        Type character = T("Sandbox.Game.Entities.Character.MyCharacter");
        if (character == null) { Log("walk patch skipped (character type not found)"); return; }
        MethodInfo target = null;
        foreach (MethodInfo m in character.GetMethods(All | BindingFlags.DeclaredOnly))
        {
          if (m.Name != "MoveAndRotate") continue;
          ParameterInfo[] ps = m.GetParameters();
          if (ps.Length == 3 && ps[0].ParameterType == typeof(Vector3)) { target = m; break; }
        }
        if (target == null) { Log("walk patch skipped (MoveAndRotate not found)"); return; }
        DoPatch(target, new HarmonyMethod(typeof(Plugin).GetMethod("WalkPrefix", BindingFlags.Static | BindingFlags.Public)), null);
        Log("walk patch applied");
      }
      catch (Exception e)
      {
        Log("walk patch failed: " + e.Message);
      }
    }

    // Harmony's Patch() gained extra optional parameters between versions, so it is called by parameter
    // name and works with whatever Harmony version the loader brought (Pulsar Legacy ships 2.4.x).
    private static void DoPatch(MethodBase original, HarmonyMethod prefix, HarmonyMethod transpiler)
    {
      MethodInfo best = null;
      foreach (MethodInfo m in typeof(Harmony).GetMethods(BindingFlags.Public | BindingFlags.Instance))
      {
        if (m.Name != "Patch") continue;
        ParameterInfo[] ps = m.GetParameters();
        if (ps.Length == 0 || ps[0].ParameterType != typeof(MethodBase)) continue;
        if (best == null || ps.Length > best.GetParameters().Length) best = m;
      }
      if (best == null)
        throw new MissingMethodException("Harmony.Patch(MethodBase, ...) not found");

      ParameterInfo[] pars = best.GetParameters();
      object[] args = new object[pars.Length];
      args[0] = original;
      for (int i = 1; i < pars.Length; i++)
      {
        string n = (pars[i].Name ?? "").ToLowerInvariant();
        if (n == "prefix") args[i] = prefix;
        else if (n == "transpiler") args[i] = transpiler;
        else args[i] = null;
      }
      best.Invoke(_harmony, args);
    }

    private delegate IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> ins);

    private static void PatchUse(string typeName, string method, Transpile t)
    {
      Type type = T(typeName);
      MethodInfo target = null;
      if (type != null)
      {
        // the real work is the overload that takes a bool and is declared on this type itself
        foreach (MethodInfo m in type.GetMethods(All | BindingFlags.DeclaredOnly))
          if (m.Name == method && m.GetParameters().Length >= 1 && m.GetMethodBody() != null &&
              m.GetMethodBody().GetILAsByteArray().Length > 60)
            target = m;
      }
      if (target == null)
      {
        Log("use patch skipped for " + typeName);
        return;
      }
      bool shape = typeName.EndsWith("ShapecastDetectorComponent");
      string which = shape ? "ShapeTranspiler" : "RayTranspiler";
      try
      {
        // the patch reads and writes two Vector3D locals; refuse to patch if the game changed their types
        var locals = target.GetMethodBody().LocalVariables;
        int a = shape ? 1 : 2, b = shape ? 2 : 3;
        if (locals.Count <= b || locals[a].LocalType != typeof(Vector3D) || locals[b].LocalType != typeof(Vector3D))
        {
          Log("use patch skipped for " + typeName + " (local variables are not what was expected)");
          return;
        }
        DoPatch(target, null, new HarmonyMethod(typeof(Plugin).GetMethod(which, BindingFlags.Static | BindingFlags.Public)));
        Log("use patch applied: " + typeName);
      }
      catch (Exception e)
      {
        Log("use patch failed for " + typeName + ": " + e.Message);
      }
    }

    // ------------------------------------------------------------------ helpers used by the patches

    private static bool IsLocal(object character)
    {
      object s = _sessionStatic == null ? null : _sessionStatic.GetValue(null, null);
      if (s == null) return false;
      return ReferenceEquals(_localCharacter.GetValue(s, null), character);
    }

    private static bool CameraRay(out Vector3D pos, out Vector3D fwd)
    {
      pos = Vector3D.Zero;
      fwd = Vector3D.Forward;
      object cam = GetValue(_mainCamera, null);
      if (cam == null) return false;
      MatrixD wm = (MatrixD)GetValue(_camWorldMatrix, cam);
      pos = wm.Translation;
      fwd = Vector3D.Normalize(wm.Forward);
      return true;
    }

    private static Vector3D HeadPosition(object character)
    {
      object[] args = new object[_getHeadArgs];
      for (int i = 0; i < args.Length; i++) args[i] = false;
      return ((MatrixD)_getHead.Invoke(character, args)).Translation;
    }

    // Camera ray moved forward to the depth of the head, like the game does in its own third person.
    private static Vector3D StartAtHeadDepth(Vector3D camPos, Vector3D camFwd, Vector3D head)
    {
      return camPos + camFwd * Vector3D.Dot(head - camPos, camFwd);
    }

    // ------------------------------------------------------------------ patch 1: shots

    public static bool AimPrefix(object __instance, ref Vector3D __result)
    {
      if (_aimBroken || !Active || !IsLocal(__instance))
        return true;

      if (!_aimSeen) { _aimSeen = true; Log("aim patch ran for the first time (shots follow the camera)"); }

      try
      {
        Vector3D camPos, camFwd;
        if (!CameraRay(out camPos, out camFwd))
          return true;

        Vector3D start = StartAtHeadDepth(camPos, camFwd, HeadPosition(__instance));

        Vector3D result;
        object wp = _getWeaponPos == null ? null : _getWeaponPos.Invoke(__instance, null);
        Vector3D farBase = (wp != null && _logicalPos != null) ? (Vector3D)_logicalPos.GetValue(wp, null) : start;
        result = farBase + camFwd * 25000.0;

        object weapon = _getCurrentWeapon == null ? null : _getCurrentWeapon.Invoke(__instance, null);
        if (weapon != null && _castRay != null && _hitPosition != null)
        {
          float len = 100f;
          PropertyInfo shot = FindProperty(weapon.GetType(), "MaximumShotLength");
          if (shot != null)
            len = Math.Min(Convert.ToSingle(shot.GetValue(weapon, null)), 100f);

          object hit = _castRay.Invoke(null, new object[] { start, start + camFwd * len, 30 });
          if (hit != null)
            result = (Vector3D)GetValue(_hitPosition, hit);
        }

        __result = result;
        return false;
      }
      catch (Exception e)
      {
        _aimBroken = true;
        Log("aim patch switched off after an error: " + e);
        return true;
      }
    }

    private static PropertyInfo FindProperty(Type t, string name)
    {
      PropertyInfo p = t.GetProperty(name, All);
      if (p != null) return p;
      foreach (Type i in t.GetInterfaces())
      {
        p = i.GetProperty(name, All);
        if (p != null) return p;
      }
      return null;
    }

    // ------------------------------------------------------------------ patch 2: use / interaction

    // Called from the patched detectors: replaces the ray start / direction when a camera mod is active.
    public static Vector3D UseStart(Vector3D current, object detectorCharacter)
    {
      if (!Active) return current;
      try
      {
        object s = _sessionStatic.GetValue(null, null);
        object ch = _localCharacter.GetValue(s, null);
        Vector3D camPos, camFwd;
        if (ch == null || !CameraRay(out camPos, out camFwd)) return current;
        return StartAtHeadDepth(camPos, camFwd, HeadPosition(ch));
      }
      catch (Exception) { return current; }
    }

    public static Vector3D UseDirection(Vector3D current)
    {
      if (!Active) return current;
      if (!_useSeen) { _useSeen = true; Log("use patch ran for the first time (use ray follows the camera)"); }
      try
      {
        Vector3D camPos, camFwd;
        return CameraRay(out camPos, out camFwd) ? camFwd : current;
      }
      catch (Exception) { return current; }
    }

    private static readonly MethodInfo MiStart = typeof(Plugin).GetMethod("UseStartLocal", BindingFlags.Static | BindingFlags.Public);
    private static readonly MethodInfo MiDir = typeof(Plugin).GetMethod("UseDirection", BindingFlags.Static | BindingFlags.Public);

    public static Vector3D UseStartLocal(Vector3D current)
    {
      return UseStart(current, null);
    }

    // Insert  (start = UseStartLocal(start); dir = UseDirection(dir))  at index i, keeping jump labels.
    private static List<CodeInstruction> Insert(List<CodeInstruction> code, int i, OpCode startLoad, OpCode startStore,
                                               OpCode dirLoad, OpCode dirStore)
    {
      var add = new List<CodeInstruction>
      {
        new CodeInstruction(startLoad),
        new CodeInstruction(OpCodes.Call, MiStart),
        new CodeInstruction(startStore),
        new CodeInstruction(dirLoad),
        new CodeInstruction(OpCodes.Call, MiDir),
        new CodeInstruction(dirStore)
      };
      add[0].MoveLabelsFrom(code[i]);
      code.InsertRange(i, add);
      return code;
    }

    // Shape cast (finds blocks such as doors and buttons). Locals: 1 = start, 2 = direction, then
    // "start + direction * 2.5" is built.
    public static IEnumerable<CodeInstruction> ShapeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      var code = new List<CodeInstruction>(instructions);
      for (int i = 2; i < code.Count; i++)
      {
        if (code[i].opcode == OpCodes.Ldc_R8 && Math.Abs((double)code[i].operand - 2.5) < 1e-9 &&
            code[i - 1].opcode == OpCodes.Ldloc_2 && code[i - 2].opcode == OpCodes.Ldloc_1)
        {
          Log("shape cast patch site found");
          return Insert(code, i - 2, OpCodes.Ldloc_1, OpCodes.Stloc_1, OpCodes.Ldloc_2, OpCodes.Stloc_2);
        }
      }
      Log("shape cast patch site NOT found - use patch off for shape cast");
      return code;
    }

    // Ray cast detector. Locals: 2 = start, 3 = direction, then "start + direction * interactive distance".
    public static IEnumerable<CodeInstruction> RayTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      var code = new List<CodeInstruction>(instructions);
      for (int i = 2; i < code.Count; i++)
      {
        FieldInfo f = code[i].operand as FieldInfo;
        if (code[i].opcode == OpCodes.Ldsfld && f != null && f.Name == "DEFAULT_INTERACTIVE_DISTANCE" &&
            code[i - 1].opcode == OpCodes.Ldloc_3 && code[i - 2].opcode == OpCodes.Ldloc_2)
        {
          Log("ray cast patch site found");
          return Insert(code, i - 2, OpCodes.Ldloc_2, OpCodes.Stloc_2, OpCodes.Ldloc_3, OpCodes.Stloc_3);
        }
      }
      Log("ray cast patch site NOT found - use patch off for ray cast");
      return code;
    }

    // ------------------------------------------------------------------ log

    private static void Log(string s)
    {
      try { MyLog.Default.WriteLineAndConsole(Tag + s); } catch (Exception) { }
    }
  }
}
