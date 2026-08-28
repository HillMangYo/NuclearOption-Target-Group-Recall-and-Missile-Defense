using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using Rewired;
using UnityEngine;

namespace NuclearOption.TargetGroupRecallAndMissileDefense;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(TactitoolsGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class TargetGroupRecallAndMissileDefensePlugin : BaseUnityPlugin
{
    public const string PluginGuid = "nuclearoption.targetgrouprecallandmissiledefense";
    public const string PluginName = "Nuclear Option: Target Group Recall and Missile Defense";
    public const string PluginVersion = "1.0.0";

    private const int SavedGroupCount = 2;
    private const int MaxTargetsPerGroup = 128;
    private const int KeyboardSource = 0;
    private const int ControllerSource = 1;
    private const string TactitoolsGuid = "com.george.NO_Tactitools";

    private static TargetGroupRecallAndMissileDefensePlugin Instance;

    private readonly PersistentID[][] _savedGroups = new PersistentID[SavedGroupCount][];
    private readonly PressState[,] _pressStates = new PressState[SavedGroupCount, 2];
    private readonly int[] _lastActionFrame = new int[SavedGroupCount];
    private readonly OneShotPressState[] _defensePressStates =
    {
        new OneShotPressState(),
        new OneShotPressState()
    };

    private ConfigEntry<bool> _enabled;
    private ConfigEntry<float> _longPressSeconds;
    private ConfigEntry<bool> _showStatusMessages;
    private ConfigEntry<bool> _verboseLogging;
    private ConfigEntry<bool> _suppressTactitoolsRecall;
    private GroupConfig[] _groups;
    private MissileDefenseConfig _missileDefense;
    private Harmony _harmony;
    private bool _controllerReadErrorLogged;
    private int _lastDefenseActionFrame = -1;
    private PersistentID[] _lastDefenseSelection = Array.Empty<PersistentID>();
    private readonly bool[] _defenseConflictLogged = new bool[2];
    private readonly bool[] _groupWasDisabled = new bool[SavedGroupCount];
    private bool _inputWasUnavailable = true;
    private bool _defenseWasDisabled;
    private bool _suppressDPadUpCountermeasureRelease;
    private int _countermeasureSuppressionReleaseFrame = -1;
    private bool _dPadUpDefenseHoldCompleted;

    private void Awake()
    {
        Instance = this;
        string preUpgradeConfigText = File.Exists(Config.ConfigFilePath)
            ? File.ReadAllText(Config.ConfigFilePath)
            : string.Empty;
        float? legacyDefenseHoldDefault =
            GetLegacyDefenseHoldDefault(preUpgradeConfigText);

        _enabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Enable saved target groups and Missile Defense.");

        _longPressSeconds = Config.Bind(
            "General",
            "LongPressSeconds",
            0.4f,
            new ConfigDescription(
                "Hold duration that saves a group. Releasing sooner recalls it.",
                new AcceptableValueRange<float>(0.15f, 2f)));

        _showStatusMessages = Config.Bind(
            "General",
            "ShowStatusMessages",
            true,
            "Show save/recall results in the aircraft action-message area.");

        _verboseLogging = Config.Bind(
            "General",
            "VerboseLogging",
            false,
            "Write extra input and lifecycle details to the BepInEx log.");

        _suppressTactitoolsRecall = Config.Bind(
            "Compatibility",
            "SuppressNOTactitoolsTargetRecall",
            true,
            "When NO Tactitools' Target List Controller is enabled, suppress only its old Remember/Recall callbacks so the shared D-pad button is not handled twice. Other NO Tactitools target controls remain active.");

        _groups = new[]
        {
            new GroupConfig(
                Config,
                1,
                enabledByDefault: true,
                new KeyboardShortcut(KeyCode.L),
                GamepadControl.DPadLeft),
            new GroupConfig(
                Config,
                2,
                enabledByDefault: false,
                KeyboardShortcut.Empty,
                GamepadControl.None)
        };

        // v1.0 exposed the third input as a normal saved group. Carry any custom
        // bindings into the new one-shot Missile Defense action, then remove the
        // obsolete Group 3 section so the configuration reflects the new model.
        (bool hadLegacyGroup3, KeyboardShortcut legacyDefenseKeyboard, GamepadControl legacyDefenseController) =
            ReadAndRemoveLegacyGroup3Bindings();
        _missileDefense = new MissileDefenseConfig(
            Config,
            legacyDefenseKeyboard,
            legacyDefenseController,
            hadLegacyGroup3,
            legacyDefenseHoldDefault);

        for (int group = 0; group < SavedGroupCount; group++)
        {
            _savedGroups[group] = Array.Empty<PersistentID>();
            _lastActionFrame[group] = -1;
            _pressStates[group, KeyboardSource] = new PressState();
            _pressStates[group, ControllerSource] = new PressState();
        }

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(TargetGroupRecallAndMissileDefensePlugin).Assembly);
        ConfigureTactitoolsCompatibility();

        Logger.LogInfo(
            $"{PluginName} v{PluginVersion} loaded. Group 1: keyboard L / controller D-pad Left. " +
            "Group 2 is optional; Missile Defense is enabled and uses the former Group 3 binding.");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        ResetPressStates();
        ClearSavedGroups("plugin unloaded");

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void Update()
    {
        ExpireCountermeasureSuppression();

        if (!_enabled.Value)
        {
            ClearCountermeasureSuppression();
            _inputWasUnavailable = true;
            BlockActiveInputsUntilRelease();
            return;
        }

        if (!CanProcessInput())
        {
            _inputWasUnavailable = true;
            BlockActiveInputsUntilRelease();
            return;
        }

        IList<IGamepadTemplate> gamepads = GetGamepads();

        if (_inputWasUnavailable)
        {
            _inputWasUnavailable = false;
            BlockCurrentlyHeldInputs(gamepads);
        }

        for (int groupIndex = 0; groupIndex < SavedGroupCount; groupIndex++)
        {
            GroupConfig group = _groups[groupIndex];
            if (!group.Enabled.Value)
            {
                _groupWasDisabled[groupIndex] = true;
                ResetGroupPressStates(groupIndex);
                continue;
            }

            if (_groupWasDisabled[groupIndex])
            {
                _groupWasDisabled[groupIndex] = false;
                BlockCurrentlyHeldGroupInputs(groupIndex, gamepads);
            }

            KeyboardShortcut shortcut = group.Keyboard.Value;
            if (shortcut.MainKey == KeyCode.None)
            {
                _pressStates[groupIndex, KeyboardSource].Reset();
            }
            else
            {
                UpdatePressState(
                    groupIndex,
                    KeyboardSource,
                    IsShortcutHeld(shortcut));
            }

            GamepadControl control = group.Controller.Value;
            if (control == GamepadControl.None || !TryReadGamepadControl(gamepads, control, out bool controllerHeld))
            {
                // A disconnected controller must not look like a deliberate button release.
                PressState state = _pressStates[groupIndex, ControllerSource];
                if (state.WasHeld)
                {
                    state.BlockedUntilRelease = true;
                }
            }
            else
            {
                UpdatePressState(groupIndex, ControllerSource, controllerHeld);
            }
        }

        UpdateMissileDefenseInput(gamepads);
    }

    private void BlockCurrentlyHeldGroupInputs(int groupIndex, IList<IGamepadTemplate> gamepads)
    {
        GroupConfig config = _groups[groupIndex];
        KeyboardShortcut keyboard = config.Keyboard.Value;
        if (keyboard.MainKey != KeyCode.None && IsShortcutHeld(keyboard))
        {
            _pressStates[groupIndex, KeyboardSource].WasHeld = true;
            _pressStates[groupIndex, KeyboardSource].BlockedUntilRelease = true;
        }

        GamepadControl controller = config.Controller.Value;
        if (controller != GamepadControl.None &&
            TryReadGamepadControl(gamepads, controller, out bool held) && held)
        {
            _pressStates[groupIndex, ControllerSource].WasHeld = true;
            _pressStates[groupIndex, ControllerSource].BlockedUntilRelease = true;
        }
    }

    private void BlockActiveInputsUntilRelease()
    {
        for (int group = 0; group < SavedGroupCount; group++)
        {
            for (int source = 0; source < 2; source++)
            {
                PressState state = _pressStates[group, source];
                if (state.WasHeld)
                {
                    state.BlockedUntilRelease = true;
                }
            }
        }

        for (int source = 0; source < 2; source++)
        {
            OneShotPressState state = _defensePressStates[source];
            if (state.WasHeld)
            {
                state.BlockedUntilRelease = true;
            }
        }
    }

    private void BlockCurrentlyHeldInputs(IList<IGamepadTemplate> gamepads)
    {
        for (int group = 0; group < SavedGroupCount; group++)
        {
            GroupConfig config = _groups[group];
            KeyboardShortcut keyboard = config.Keyboard.Value;
            if (keyboard.MainKey != KeyCode.None && IsShortcutHeld(keyboard))
            {
                _pressStates[group, KeyboardSource].WasHeld = true;
                _pressStates[group, KeyboardSource].BlockedUntilRelease = true;
            }

            GamepadControl controller = config.Controller.Value;
            if (controller != GamepadControl.None &&
                TryReadGamepadControl(gamepads, controller, out bool groupControllerHeld) &&
                groupControllerHeld)
            {
                _pressStates[group, ControllerSource].WasHeld = true;
                _pressStates[group, ControllerSource].BlockedUntilRelease = true;
            }
        }

        KeyboardShortcut defenseKeyboard = _missileDefense.Keyboard.Value;
        if (defenseKeyboard.MainKey != KeyCode.None && IsShortcutHeld(defenseKeyboard))
        {
            _defensePressStates[KeyboardSource].WasHeld = true;
            _defensePressStates[KeyboardSource].BlockedUntilRelease = true;
        }

        GamepadControl defenseController = _missileDefense.Controller.Value;
        if (defenseController != GamepadControl.None &&
            TryReadGamepadControl(gamepads, defenseController, out bool defenseControllerHeld) &&
            defenseControllerHeld)
        {
            _defensePressStates[ControllerSource].WasHeld = true;
            _defensePressStates[ControllerSource].BlockedUntilRelease = true;
        }
    }

    private void UpdateMissileDefenseInput(IList<IGamepadTemplate> gamepads)
    {
        if (!_missileDefense.Enabled.Value)
        {
            _defenseWasDisabled = true;
            ClearCountermeasureSuppression();
            ResetDefensePressStates();
            return;
        }

        if (_defenseWasDisabled)
        {
            _defenseWasDisabled = false;
            BlockCurrentlyHeldDefenseInputs(gamepads);
        }

        bool radialMenuInUse = IsRadialMenuEngaged();

        KeyboardShortcut keyboard = _missileDefense.Keyboard.Value;
        if (keyboard.MainKey == KeyCode.None)
        {
            _defensePressStates[KeyboardSource].Reset();
        }
        else
        {
            bool conflicts = DefenseKeyboardConflictsWithSavedGroup(keyboard);
            ReportDefenseBindingConflict(KeyboardSource, conflicts);
            UpdateDefensePressState(
                KeyboardSource,
                IsShortcutHeld(keyboard),
                radialMenuInUse || conflicts);
        }

        GamepadControl controller = _missileDefense.Controller.Value;
        if (controller != GamepadControl.DPadUp && _dPadUpDefenseHoldCompleted)
        {
            ClearCountermeasureSuppression();
        }

        if (controller == GamepadControl.None ||
            !TryReadGamepadControl(gamepads, controller, out bool controllerHeld))
        {
            OneShotPressState state = _defensePressStates[ControllerSource];
            if (state.WasHeld)
            {
                state.BlockedUntilRelease = true;
            }

            if (controller == GamepadControl.DPadUp)
            {
                ClearCountermeasureSuppression();
            }
        }
        else
        {
            bool conflicts = DefenseControllerConflictsWithSavedGroup(controller);
            ReportDefenseBindingConflict(ControllerSource, conflicts);
            UpdateDefensePressState(
                ControllerSource,
                controllerHeld,
                radialMenuInUse || conflicts);
        }
    }

    private void BlockCurrentlyHeldDefenseInputs(IList<IGamepadTemplate> gamepads)
    {
        KeyboardShortcut keyboard = _missileDefense.Keyboard.Value;
        if (keyboard.MainKey != KeyCode.None && IsShortcutHeld(keyboard))
        {
            _defensePressStates[KeyboardSource].WasHeld = true;
            _defensePressStates[KeyboardSource].BlockedUntilRelease = true;
        }

        GamepadControl controller = _missileDefense.Controller.Value;
        if (controller != GamepadControl.None &&
            TryReadGamepadControl(gamepads, controller, out bool held) && held)
        {
            _defensePressStates[ControllerSource].WasHeld = true;
            _defensePressStates[ControllerSource].BlockedUntilRelease = true;
        }
    }

    private void UpdateDefensePressState(int sourceIndex, bool held, bool blocked)
    {
        OneShotPressState state = _defensePressStates[sourceIndex];

        if (blocked)
        {
            if (held)
            {
                state.BlockedUntilRelease = true;
                state.WasHeld = true;
            }
            else
            {
                if (sourceIndex == ControllerSource && _dPadUpDefenseHoldCompleted)
                {
                    ArmDPadUpCountermeasureSuppression();
                    _countermeasureSuppressionReleaseFrame = Time.frameCount;
                }

                state.Reset();
            }

            return;
        }

        if (state.BlockedUntilRelease)
        {
            if (!held)
            {
                if (sourceIndex == ControllerSource && _dPadUpDefenseHoldCompleted)
                {
                    ArmDPadUpCountermeasureSuppression();
                    _countermeasureSuppressionReleaseFrame = Time.frameCount;
                }

                state.Reset();
            }

            return;
        }

        if (!state.WasHeld)
        {
            if (held)
            {
                state.WasHeld = true;
                state.StartedAt = Time.unscaledTime;
                state.LongPressHandled = false;
                Verbose($"Missile Defense {SourceName(sourceIndex)} hold started.");
            }

            return;
        }

        float holdThreshold = _missileDefense.HoldSeconds.Value;

        if (held)
        {
            if (!state.LongPressHandled && Time.unscaledTime - state.StartedAt >= holdThreshold)
            {
                state.LongPressHandled = true;
                HandleMissileDefense(sourceIndex);
            }

            return;
        }

        bool longPressWasHandled = state.LongPressHandled;
        float duration = Time.unscaledTime - state.StartedAt;

        // Handle a threshold crossing first observed on release before clearing the
        // state, so the countermeasure-overlap guard covers either Unity Update order.
        if (!longPressWasHandled && duration >= holdThreshold)
        {
            state.LongPressHandled = true;
            longPressWasHandled = true;
            HandleMissileDefense(sourceIndex);
        }

        if (sourceIndex == ControllerSource && longPressWasHandled &&
            _missileDefense.Controller.Value == GamepadControl.DPadUp)
        {
            // Keep suppression alive through this frame regardless of whether the
            // stock player-controls Update runs before or after this plugin Update.
            ArmDPadUpCountermeasureSuppression();
            _countermeasureSuppressionReleaseFrame = Time.frameCount;
        }

        state.Reset();
    }

    private bool CanProcessInput()
    {
        if (!Application.isFocused || GameplayUI.GameIsPaused || InputFieldChecker.InsideInputField ||
            !GameManager.flightControlsEnabled)
        {
            return false;
        }

        CombatHUD hud = SceneSingleton<CombatHUD>.i;
        if (hud == null || !hud.isActiveAndEnabled || hud.aircraft == null || hud.aircraft.disabled ||
            SceneSingleton<DynamicMap>.i == null || SceneSingleton<TargetListSelector>.i == null)
        {
            return false;
        }

        return GameManager.GetLocalAircraft(out Aircraft localAircraft) && localAircraft == hud.aircraft;
    }

    private static bool IsRadialMenuEngaged()
    {
        return (SceneSingleton<RadialMenuMain>.i != null && RadialMenuMain.IsInUse()) ||
            (GameManager.playerInput != null && GameManager.playerInput.GetButton("Radial Menu"));
    }

    private void UpdatePressState(int groupIndex, int sourceIndex, bool held)
    {
        PressState state = _pressStates[groupIndex, sourceIndex];

        if (state.BlockedUntilRelease)
        {
            if (!held)
            {
                state.Reset();
            }

            return;
        }

        if (!state.WasHeld)
        {
            if (held)
            {
                state.WasHeld = true;
                state.StartedAt = Time.unscaledTime;
                state.LongPressHandled = false;
                Verbose($"Group {groupIndex + 1} {SourceName(sourceIndex)} press started.");
            }

            return;
        }

        if (held)
        {
            if (!state.LongPressHandled && Time.unscaledTime - state.StartedAt >= _longPressSeconds.Value)
            {
                state.LongPressHandled = true;
                HandleGroupAction(groupIndex, save: true);
            }

            return;
        }

        bool longPressWasHandled = state.LongPressHandled;
        float duration = Time.unscaledTime - state.StartedAt;
        state.Reset();

        // Classify from the measured duration as well as the in-hold latch. This
        // handles a release that lands on the first frame after the threshold.
        if (!longPressWasHandled && duration >= _longPressSeconds.Value)
        {
            HandleGroupAction(groupIndex, save: true);
        }
        else if (!longPressWasHandled)
        {
            Verbose($"Group {groupIndex + 1} {SourceName(sourceIndex)} quick press released after {duration:0.000}s.");
            HandleGroupAction(groupIndex, save: false);
        }
    }

    private void HandleGroupAction(int groupIndex, bool save)
    {
        // Keyboard and controller are tracked separately. This suppresses only an
        // accidental duplicate when both sources complete on the same frame.
        if (_lastActionFrame[groupIndex] == Time.frameCount)
        {
            return;
        }

        _lastActionFrame[groupIndex] = Time.frameCount;

        try
        {
            if (save)
            {
                SaveGroup(groupIndex);
            }
            else
            {
                RecallGroup(groupIndex);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Group {groupIndex + 1} {(save ? "save" : "recall")} failed: {ex}");
            Notify($"Target group {groupIndex + 1} {(save ? "save" : "recall")} failed.", log: false);
        }
    }

    private void HandleMissileDefense(int sourceIndex)
    {
        if (sourceIndex == ControllerSource &&
            _missileDefense.Controller.Value == GamepadControl.DPadUp)
        {
            // Nuclear Option accepts D-pad Up releases below clickDelay as "Next
            // Countermeasure". Keep the exact boundary clean even when frame timing
            // differs by suppressing only the release after this hold actually fires.
            _dPadUpDefenseHoldCompleted = true;
            ArmDPadUpCountermeasureSuppression();
        }

        // Keyboard and controller can be held together; run the emergency action
        // only once when both hold thresholds complete on the same frame.
        if (_lastDefenseActionFrame == Time.frameCount)
        {
            return;
        }

        _lastDefenseActionFrame = Time.frameCount;

        try
        {
            SelectIncomingMissiles();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Missile Defense selection failed: {ex}");
            Notify("Missile Defense selection failed.", log: false);
        }
    }

    private void ArmDPadUpCountermeasureSuppression()
    {
        _suppressDPadUpCountermeasureRelease = true;
        _countermeasureSuppressionReleaseFrame = -1;
    }

    private void ExpireCountermeasureSuppression()
    {
        if (!_suppressDPadUpCountermeasureRelease)
        {
            return;
        }

        if (_countermeasureSuppressionReleaseFrame >= 0 &&
            Time.frameCount > _countermeasureSuppressionReleaseFrame)
        {
            ClearCountermeasureSuppression();
        }
    }

    private void ClearCountermeasureSuppression()
    {
        _suppressDPadUpCountermeasureRelease = false;
        _countermeasureSuppressionReleaseFrame = -1;
        _dPadUpDefenseHoldCompleted = false;
    }

    private bool ShouldSuppressNextCountermeasure(CountermeasureManager countermeasureManager)
    {
        if (!_enabled.Value || !_missileDefense.Enabled.Value ||
            _missileDefense.Controller.Value != GamepadControl.DPadUp)
        {
            return false;
        }

        if (!GameManager.GetLocalAircraft(out Aircraft localAircraft) ||
            !ReferenceEquals(localAircraft.countermeasureManager, countermeasureManager))
        {
            return false;
        }

        bool dPadUpJustReleased = false;
        foreach (IGamepadTemplate gamepad in GetGamepads())
        {
            IControllerTemplateButton dPadUp = ResolveButton(gamepad, GamepadControl.DPadUp);
            if (dPadUp != null && dPadUp.exists && dPadUp.justReleased)
            {
                dPadUpJustReleased = true;
                break;
            }
        }

        if (!dPadUpJustReleased)
        {
            return false;
        }

        OneShotPressState controllerState = _defensePressStates[ControllerSource];
        bool releaseReachedHoldThreshold =
            controllerState.WasHeld && !controllerState.BlockedUntilRelease &&
            Time.unscaledTime - controllerState.StartedAt >= _missileDefense.HoldSeconds.Value;
        bool suppressionArmed =
            _suppressDPadUpCountermeasureRelease ||
            _countermeasureSuppressionReleaseFrame == Time.frameCount;

        if (!suppressionArmed && !releaseReachedHoldThreshold)
        {
            return false;
        }

        if (!suppressionArmed)
        {
            // Native PlayerControls ran before this plugin's Update on the release
            // frame. Complete only a currently eligible hold before suppressing.
            bool radialMenuInUse = IsRadialMenuEngaged();
            if (!CanProcessInput() || radialMenuInUse ||
                DefenseControllerConflictsWithSavedGroup(GamepadControl.DPadUp))
            {
                return false;
            }

            controllerState.LongPressHandled = true;
            HandleMissileDefense(ControllerSource);
        }

        ClearCountermeasureSuppression();
        Verbose("Suppressed the stock countermeasure cycle after a Missile Defense hold.");
        return true;
    }

    private void SelectIncomingMissiles()
    {
        if (!TryGetTargetContext(out CombatHUD hud, out Aircraft aircraft))
        {
            return;
        }

        MissileWarning warning = aircraft.GetMissileWarningSystem();
        if (warning == null)
        {
            Notify("No incoming missiles detected.");
            return;
        }

        List<Missile> incoming = new List<Missile>();
        HashSet<PersistentID> seen = new HashSet<PersistentID>();

        // The stock warning system exposes only missiles the player has actually
        // detected as incoming. Recheck target state because its cleanup is periodic.
        foreach (Missile missile in new List<Missile>(warning.knownMissiles))
        {
            if (missile == null || missile.disabled ||
                missile.targetID != aircraft.persistentID ||
                !missile.persistentID.IsValid || !seen.Add(missile.persistentID))
            {
                continue;
            }

            if (!hud.TryGetMarker(missile, out HUDUnitMarker marker) || marker == null)
            {
                hud.CreateMarker(missile.persistentID);
            }

            if (hud.TryGetMarker(missile, out marker) && marker != null)
            {
                incoming.Add(missile);
            }
        }

        // Match the game's own defensive threat choice: nearest incoming first.
        incoming.Sort((left, right) =>
            FastMath.SquareDistance(left.GlobalPosition(), aircraft.GlobalPosition())
                .CompareTo(FastMath.SquareDistance(right.GlobalPosition(), aircraft.GlobalPosition())));

        if (incoming.Count > MaxTargetsPerGroup)
        {
            incoming.RemoveRange(MaxTargetsPerGroup, incoming.Count - MaxTargetsPerGroup);
        }

        if (incoming.Count == 0)
        {
            Notify("No incoming missiles detected.");
            return;
        }

        PersistentID[] displacedSelection = CaptureCurrentSelection(hud, aircraft);
        bool savedDisplacedSelection = false;

        if (_missileDefense.AutoSaveCurrentSelectionToGroup1.Value &&
            displacedSelection.Length > 0 &&
            !SelectionIsAlreadyDefensive(displacedSelection, aircraft))
        {
            _savedGroups[0] = displacedSelection;
            savedDisplacedSelection = true;
        }

        List<Unit> replacement = incoming.Cast<Unit>().ToList();
        ReplaceTargetSelection(hud, aircraft, replacement);
        _lastDefenseSelection = incoming.Select(missile => missile.persistentID).ToArray();

        string savedSuffix = savedDisplacedSelection
            ? " Previous selection saved to group 1."
            : string.Empty;
        Notify(
            $"Missile Defense targeted {incoming.Count} incoming missile{Plural(incoming.Count)}." +
            savedSuffix);
    }

    private bool SelectionIsAlreadyDefensive(PersistentID[] selection, Aircraft aircraft)
    {
        if (selection.Length == 0)
        {
            return false;
        }

        HashSet<PersistentID> previousDefense = new HashSet<PersistentID>(_lastDefenseSelection);

        foreach (PersistentID id in selection)
        {
            if (previousDefense.Contains(id))
            {
                continue;
            }

            if (!UnitRegistry.TryGetUnit(id, out Unit unit) ||
                unit is not Missile missile || missile.disabled ||
                missile.targetID != aircraft.persistentID)
            {
                return false;
            }
        }

        return true;
    }

    private void SaveGroup(int groupIndex)
    {
        if (!TryGetTargetContext(out CombatHUD hud, out Aircraft aircraft))
        {
            return;
        }

        PersistentID[] snapshot = CaptureCurrentSelection(hud, aircraft);

        // D-pad Left is also the normal Aircraft Systems hold. Do not erase a useful
        // group merely because the radial menu was used with no targets selected.
        if (snapshot.Length == 0)
        {
            Notify($"Target group {groupIndex + 1} not changed: no targets selected.");
            return;
        }

        _savedGroups[groupIndex] = snapshot;
        Notify($"Target group {groupIndex + 1} saved: {snapshot.Length} target{Plural(snapshot.Length)}.");
    }

    private static PersistentID[] CaptureCurrentSelection(CombatHUD hud, Aircraft aircraft)
    {
        List<Unit> targetList = hud.GetTargetList();
        List<PersistentID> snapshot = new List<PersistentID>(Math.Min(targetList.Count, MaxTargetsPerGroup));
        HashSet<PersistentID> seen = new HashSet<PersistentID>();

        foreach (Unit target in targetList)
        {
            if (target == null || target.disabled || target == aircraft)
            {
                continue;
            }

            PersistentID id = target.persistentID;
            if (!id.IsValid || !seen.Add(id))
            {
                continue;
            }

            snapshot.Add(id);
            if (snapshot.Count == MaxTargetsPerGroup)
            {
                break;
            }
        }

        return snapshot.ToArray();
    }

    private void RecallGroup(int groupIndex)
    {
        PersistentID[] saved = _savedGroups[groupIndex];
        if (saved == null || saved.Length == 0)
        {
            Notify($"Target group {groupIndex + 1} has not been saved.");
            return;
        }

        if (!TryGetTargetContext(out CombatHUD hud, out Aircraft aircraft))
        {
            return;
        }

        List<Unit> available = new List<Unit>(saved.Length);
        HashSet<PersistentID> seen = new HashSet<PersistentID>();

        foreach (PersistentID id in saved)
        {
            if (!id.IsValid || !seen.Add(id) || !UnitRegistry.TryGetUnit(id, out Unit target))
            {
                continue;
            }

            if (target == null || target.disabled || target == aircraft || !hud.TryGetMarker(target, out HUDUnitMarker marker) || marker == null)
            {
                continue;
            }

            available.Add(target);
            if (available.Count == MaxTargetsPerGroup)
            {
                break;
            }
        }

        ReplaceTargetSelection(hud, aircraft, available);

        int unavailable = saved.Length - available.Count;
        if (unavailable > 0)
        {
            Notify($"Target group {groupIndex + 1} recalled: {available.Count}/{saved.Length} target{Plural(saved.Length)} available.");
        }
        else
        {
            Notify($"Target group {groupIndex + 1} recalled: {available.Count} target{Plural(available.Count)}.");
        }
    }

    private static void ReplaceTargetSelection(CombatHUD hud, Aircraft aircraft, IList<Unit> replacement)
    {
        List<Unit> liveTargets = aircraft.weaponManager.GetTargetList();

        // Replace, rather than merge with, the current selection. This mirrors the
        // visual part of CombatHUD.DeselectAll without sending a transient empty
        // network target list before the replacement batch is ready.
        foreach (Unit currentTarget in new List<Unit>(liveTargets))
        {
            if (currentTarget != null &&
                hud.TryGetMarker(currentTarget, out HUDUnitMarker currentMarker) &&
                currentMarker != null)
            {
                currentMarker.DeselectMarker();
            }
        }

        DynamicMap map = SceneSingleton<DynamicMap>.i;
        if (map != null)
        {
            map.DeselectAllIcons();
        }

        liveTargets.Clear();
        hud.SetTargetArrow(enabled: false, Vector3.zero, Vector3.zero);

        // Preserve caller ordering, including the primary target at index zero.
        foreach (Unit target in replacement)
        {
            if (target != null && hud.TryGetMarker(target, out HUDUnitMarker marker) && marker != null)
            {
                marker.SelectMarker();
                liveTargets.Add(target);
            }
        }

        // Batch both removal and addition into one target-list notification, matching
        // the stock multi-target selection path and avoiding a transient empty update.
        aircraft.weaponManager.TargetListChanged();
    }

    private bool TryGetTargetContext(out CombatHUD hud, out Aircraft aircraft)
    {
        hud = SceneSingleton<CombatHUD>.i;
        aircraft = null;

        if (hud == null || hud.aircraft == null || hud.aircraft.disabled || hud.aircraft.weaponManager == null ||
            SceneSingleton<DynamicMap>.i == null || SceneSingleton<TargetListSelector>.i == null)
        {
            return false;
        }

        if (!GameManager.GetLocalAircraft(out aircraft) || aircraft == null || aircraft != hud.aircraft)
        {
            aircraft = null;
            return false;
        }

        return true;
    }

    private IList<IGamepadTemplate> GetGamepads()
    {
        try
        {
            if (GameManager.playerInput == null)
            {
                return Array.Empty<IGamepadTemplate>();
            }

            IList<IGamepadTemplate> gamepads = GameManager.playerInput.controllers.GetControllerTemplates<IGamepadTemplate>();
            _controllerReadErrorLogged = false;
            return gamepads ?? Array.Empty<IGamepadTemplate>();
        }
        catch (Exception ex)
        {
            if (!_controllerReadErrorLogged)
            {
                _controllerReadErrorLogged = true;
                Logger.LogWarning($"Could not read assigned gamepads; keyboard controls remain available. {ex.Message}");
            }

            return Array.Empty<IGamepadTemplate>();
        }
    }

    private static bool TryReadGamepadControl(IList<IGamepadTemplate> gamepads, GamepadControl control, out bool held)
    {
        held = false;
        bool controlExists = false;

        foreach (IGamepadTemplate gamepad in gamepads)
        {
            IControllerTemplateButton button = ResolveButton(gamepad, control);
            if (button == null || !button.exists)
            {
                continue;
            }

            controlExists = true;
            held |= button.value;
        }

        return controlExists;
    }

    private static IControllerTemplateButton ResolveButton(IGamepadTemplate gamepad, GamepadControl control)
    {
        if (gamepad == null)
        {
            return null;
        }

        return control switch
        {
            GamepadControl.DPadLeft => gamepad.dPad?.left,
            GamepadControl.DPadRight => gamepad.dPad?.right,
            GamepadControl.DPadUp => gamepad.dPad?.up,
            GamepadControl.DPadDown => gamepad.dPad?.down,
            GamepadControl.A => gamepad.a,
            GamepadControl.B => gamepad.b,
            GamepadControl.X => gamepad.x,
            GamepadControl.Y => gamepad.y,
            GamepadControl.LeftBumper => gamepad.leftBumper,
            GamepadControl.RightBumper => gamepad.rightBumper,
            GamepadControl.LeftStickPress => gamepad.leftStick?.press,
            GamepadControl.RightStickPress => gamepad.rightStick?.press,
            GamepadControl.Start => gamepad.start,
            GamepadControl.Back => gamepad.back,
            GamepadControl.Guide => gamepad.guide,
            GamepadControl.LeftTrigger => gamepad.leftTrigger?.AsButton,
            GamepadControl.RightTrigger => gamepad.rightTrigger?.AsButton,
            _ => null
        };
    }

    private static bool IsShortcutHeld(KeyboardShortcut shortcut)
    {
        KeyCode mainKey = shortcut.MainKey;
        if (mainKey == KeyCode.None || !Input.GetKey(mainKey))
        {
            return false;
        }

        return shortcut.Modifiers.All(Input.GetKey);
    }

    private bool DefenseKeyboardConflictsWithSavedGroup(KeyboardShortcut defenseShortcut)
    {
        return _groups.Any(group =>
            group.Enabled.Value &&
            group.Keyboard.Value.MainKey != KeyCode.None &&
            group.Keyboard.Value.MainKey == defenseShortcut.MainKey);
    }

    private bool DefenseControllerConflictsWithSavedGroup(GamepadControl defenseControl)
    {
        return _groups.Any(group =>
            group.Enabled.Value &&
            group.Controller.Value != GamepadControl.None &&
            group.Controller.Value == defenseControl);
    }

    private void ReportDefenseBindingConflict(int sourceIndex, bool conflicts)
    {
        if (!conflicts)
        {
            _defenseConflictLogged[sourceIndex] = false;
            return;
        }

        if (_defenseConflictLogged[sourceIndex])
        {
            return;
        }

        _defenseConflictLogged[sourceIndex] = true;
        Logger.LogWarning(
            $"Missile Defense {SourceName(sourceIndex)} binding is also assigned to an enabled saved group. " +
            "Missile Defense will ignore that input until the bindings are made distinct.");
    }

    private (bool hadLegacyGroup3, KeyboardShortcut keyboard, GamepadControl controller) ReadAndRemoveLegacyGroup3Bindings()
    {
        string configText = File.Exists(Config.ConfigFilePath)
            ? File.ReadAllText(Config.ConfigFilePath)
            : string.Empty;
        bool hadLegacyGroup3 = configText.IndexOf("[Group 3]", StringComparison.OrdinalIgnoreCase) >= 0;

        ConfigEntry<bool> legacyEnabled = Config.Bind(
            "Group 3",
            "Enabled",
            false,
            "Legacy v1.0 setting; migrated to Missile Defense.");
        ConfigEntry<KeyboardShortcut> legacyKeyboard = Config.Bind(
            "Group 3",
            "KeyboardButton",
            KeyboardShortcut.Empty,
            "Legacy v1.0 setting; migrated to Missile Defense.");
        ConfigEntry<GamepadControl> legacyController = Config.Bind(
            "Group 3",
            "ControllerButton",
            GamepadControl.None,
            "Legacy v1.0 setting; migrated to Missile Defense.");

        KeyboardShortcut keyboard = legacyKeyboard.Value;
        GamepadControl controller = legacyController.Value;
        bool hasCustomLegacyBinding =
            keyboard.MainKey != KeyCode.None || controller != GamepadControl.None;

        Config.Remove(legacyEnabled.Definition);
        Config.Remove(legacyKeyboard.Definition);
        Config.Remove(legacyController.Definition);
        Config.Save();

        return (hadLegacyGroup3 && hasCustomLegacyBinding, keyboard, controller);
    }

    private static float? GetLegacyDefenseHoldDefault(string configText)
    {
        if (string.IsNullOrEmpty(configText))
        {
            return null;
        }

        float expectedPriorDefault;
        if (configText.IndexOf("Target Group Recall v1.1.2", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            expectedPriorDefault = 0.2f;
        }
        else if (configText.IndexOf("Target Group Recall v1.1.1", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            expectedPriorDefault = 0.4f;
        }
        else
        {
            return null;
        }

        bool inMissileDefenseSection = false;
        foreach (string rawLine in configText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inMissileDefenseSection =
                    line.Equals("[Missile Defense]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inMissileDefenseSection ||
                !line.StartsWith("HoldSeconds", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator >= 0 &&
                float.TryParse(
                    line.Substring(separator + 1).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float holdSeconds))
            {
                return Mathf.Abs(holdSeconds - expectedPriorDefault) < 0.0001f
                    ? expectedPriorDefault
                    : null;
            }
        }

        return null;
    }

    private void Notify(string message, bool log = true)
    {
        if (log)
        {
            Logger.LogInfo(message);
        }

        if (!_showStatusMessages.Value)
        {
            return;
        }

        AircraftActionsReport report = SceneSingleton<AircraftActionsReport>.i;
        if (report != null)
        {
            report.ReportText(message, 2.5f);
        }
    }

    private void ResetPressStates()
    {
        for (int group = 0; group < SavedGroupCount; group++)
        {
            ResetGroupPressStates(group);
        }

        ResetDefensePressStates();
    }

    private void ResetGroupPressStates(int groupIndex)
    {
        _pressStates[groupIndex, KeyboardSource]?.Reset();
        _pressStates[groupIndex, ControllerSource]?.Reset();
    }

    private void ResetDefensePressStates()
    {
        _defensePressStates[KeyboardSource].Reset();
        _defensePressStates[ControllerSource].Reset();
    }

    private void ClearSavedGroups(string reason)
    {
        for (int group = 0; group < SavedGroupCount; group++)
        {
            _savedGroups[group] = Array.Empty<PersistentID>();
            _lastActionFrame[group] = -1;
        }

        _lastDefenseSelection = Array.Empty<PersistentID>();
        _lastDefenseActionFrame = -1;
        ClearCountermeasureSuppression();
        _inputWasUnavailable = true;

        ResetPressStates();
        Verbose($"Cleared all mission-local target groups ({reason}).");
    }

    private void ConfigureTactitoolsCompatibility()
    {
        if (!Chainloader.PluginInfos.ContainsKey(TactitoolsGuid))
        {
            return;
        }

        string configPath = Path.Combine(Paths.ConfigPath, "com.george.NO_Tactitools.cfg");
        bool featureEnabled = true;

        try
        {
            if (File.Exists(configPath))
            {
                string setting = File.ReadLines(configPath)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("Target List Controller - Enabled", StringComparison.OrdinalIgnoreCase));

                if (setting != null)
                {
                    int separator = setting.IndexOf('=');
                    if (separator >= 0 && bool.TryParse(setting.Substring(separator + 1).Trim(), out bool parsed))
                    {
                        featureEnabled = parsed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Verbose($"Could not inspect NO Tactitools compatibility setting: {ex.Message}");
        }

        if (!featureEnabled)
        {
            return;
        }

        if (TryPatchTactitoolsRecallCallbacks())
        {
            Logger.LogInfo(
                $"NO Tactitools compatibility hooks installed. Its old RememberTargets/RecallTargets callbacks are {(_suppressTactitoolsRecall.Value ? "suppressed" : "currently allowed")} while Target Group Recall and Missile Defense is enabled. " +
                "Its other Target List Controller functions remain active.");
        }
        else
        {
            Logger.LogWarning(
                "NO Tactitools Target List Controller is also enabled and may handle D-pad Left twice. " +
                "To use this mod exclusively, set 'Target List Controller - Enabled = false' in " +
                "BepInEx/config/com.george.NO_Tactitools.cfg and restart the game.");
        }
    }

    private bool TryPatchTactitoolsRecallCallbacks()
    {
        try
        {
            Type targetController = AccessTools.TypeByName("NO_Tactitools.Controls.TargetListControllerPlugin");
            if (targetController == null)
            {
                return false;
            }

            MethodInfo recallTargets = AccessTools.Method(targetController, "RecallTargets");
            MethodInfo rememberTargets = AccessTools.Method(targetController, "RememberTargets");
            MethodInfo prefixMethod = AccessTools.Method(typeof(TargetGroupRecallAndMissileDefensePlugin), nameof(TactitoolsRecallPrefix));

            if (recallTargets == null || rememberTargets == null || prefixMethod == null)
            {
                return false;
            }

            HarmonyMethod prefix = new HarmonyMethod(prefixMethod);
            _harmony.Patch(recallTargets, prefix: prefix);
            _harmony.Patch(rememberTargets, prefix: prefix);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not install NO Tactitools recall compatibility patch: {ex.Message}");
            return false;
        }
    }

    private static bool TactitoolsRecallPrefix()
    {
        // Returning false skips the old callback only while this mod and its
        // compatibility option are active. Disabling this mod restores the old path.
        return Instance == null || !Instance._enabled.Value || !Instance._suppressTactitoolsRecall.Value;
    }

    private void Verbose(string message)
    {
        if (_verboseLogging.Value)
        {
            Logger.LogInfo(message);
        }
    }

    private static string SourceName(int sourceIndex) => sourceIndex == KeyboardSource ? "keyboard" : "controller";

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private sealed class PressState
    {
        public bool WasHeld;
        public bool LongPressHandled;
        public bool BlockedUntilRelease;
        public float StartedAt;

        public void Reset()
        {
            WasHeld = false;
            LongPressHandled = false;
            BlockedUntilRelease = false;
            StartedAt = 0f;
        }
    }

    private sealed class OneShotPressState
    {
        public bool WasHeld;
        public bool BlockedUntilRelease;
        public bool LongPressHandled;
        public float StartedAt;

        public void Reset()
        {
            WasHeld = false;
            BlockedUntilRelease = false;
            LongPressHandled = false;
            StartedAt = 0f;
        }
    }

    private sealed class GroupConfig
    {
        public GroupConfig(
            ConfigFile config,
            int groupNumber,
            bool enabledByDefault,
            KeyboardShortcut defaultKeyboard,
            GamepadControl defaultController)
        {
            string section = $"Group {groupNumber}";

            Enabled = config.Bind(
                section,
                "Enabled",
                enabledByDefault,
                $"Enable saved target group {groupNumber}. Group 2 is disabled by default.");

            Keyboard = config.Bind(
                section,
                "KeyboardButton",
                defaultKeyboard,
                $"Keyboard button or shortcut for group {groupNumber}. Short press recalls; hold saves.");

            Controller = config.Bind(
                section,
                "ControllerButton",
                defaultController,
                $"Standard gamepad button for group {groupNumber}. Short press recalls; hold saves. None disables controller input for this group.");
        }

        public ConfigEntry<bool> Enabled { get; }

        public ConfigEntry<KeyboardShortcut> Keyboard { get; }

        public ConfigEntry<GamepadControl> Controller { get; }
    }

    private sealed class MissileDefenseConfig
    {
        public MissileDefenseConfig(
            ConfigFile config,
            KeyboardShortcut migratedKeyboard,
            GamepadControl migratedController,
            bool migratedFromLegacyGroup3,
            float? legacyHoldDefault)
        {
            const string section = "Missile Defense";

            KeyboardShortcut defaultKeyboard = migratedFromLegacyGroup3
                ? migratedKeyboard
                : new KeyboardShortcut(KeyCode.K);
            GamepadControl defaultController = migratedFromLegacyGroup3
                ? migratedController
                : GamepadControl.DPadUp;

            Enabled = config.Bind(
                section,
                "Enabled",
                true,
                "Enable the hold-to-target incoming-missile action.");

            HoldSeconds = config.Bind(
                section,
                "HoldSeconds",
                0.25f,
                new ConfigDescription(
                    "Hold duration for Missile Defense. When D-pad Up is used, the matching release is kept from also cycling countermeasures.",
                    new AcceptableValueRange<float>(0.15f, 2f)));

            // Move generated defaults from v1.1.1/v1.1.2 to the new clean boundary,
            // while preserving every value that was actually customized.
            if (legacyHoldDefault.HasValue &&
                Mathf.Abs(HoldSeconds.Value - legacyHoldDefault.Value) < 0.0001f)
            {
                HoldSeconds.Value = 0.25f;
            }

            Keyboard = config.Bind(
                section,
                "KeyboardButton",
                defaultKeyboard,
                "Keyboard button or shortcut for Missile Defense. Hold to activate; a quick press does nothing. Empty disables keyboard input.");

            Controller = config.Bind(
                section,
                "ControllerButton",
                defaultController,
                "Standard gamepad button for Missile Defense. Hold to activate; a quick press remains available to the game. None disables controller input.");

            AutoSaveCurrentSelectionToGroup1 = config.Bind(
                section,
                "AutoSaveCurrentSelectionToGroup1",
                true,
                "Before targeting detected incoming missiles, save the displaced non-defensive selection to group 1. Enabled by default.");
        }

        public ConfigEntry<bool> Enabled { get; }

        public ConfigEntry<float> HoldSeconds { get; }

        public ConfigEntry<KeyboardShortcut> Keyboard { get; }

        public ConfigEntry<GamepadControl> Controller { get; }

        public ConfigEntry<bool> AutoSaveCurrentSelectionToGroup1 { get; }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ResetGame))]
    private static class GameResetPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            Instance?.ClearSavedGroups("game reset");
        }
    }

    [HarmonyPatch(typeof(CountermeasureManager), nameof(CountermeasureManager.NextCountermeasure))]
    private static class CountermeasureCyclePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CountermeasureManager __instance)
        {
            // Suppress only a D-pad Up release that completed this mod's defensive
            // hold. Ordinary taps and countermeasure bindings on other controls pass.
            return Instance == null || !Instance.ShouldSuppressNextCountermeasure(__instance);
        }
    }
}

public enum GamepadControl
{
    None,
    DPadLeft,
    DPadRight,
    DPadUp,
    DPadDown,
    A,
    B,
    X,
    Y,
    LeftBumper,
    RightBumper,
    LeftStickPress,
    RightStickPress,
    Start,
    Back,
    Guide,
    LeftTrigger,
    RightTrigger
}
