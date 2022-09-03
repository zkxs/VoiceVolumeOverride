using BaseX;
using CodeX;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using NeosModLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace VoiceVolumeOverride
{
    public class VoiceVolumeOverride : NeosMod
    {
        internal const string VERSION = "1.1.0";
        public override string Name => "VoiceVolumeOverride";
        public override string Author => "runtime";
        public override string Version => VERSION;
        public override string Link => "https://github.com/zkxs/VoiceVolumeOverride";

        private static readonly string VOICE_MULTIPLIER_SETTING_NAME = "Settings.Mod.VoiceVolumeOverride.PostMultiplier";
        private static readonly string UIBUILDER_FIELD_NAME = "ui";

        private static LocalModeVariableProxy<float>? _volumeMultiplier;
        private static MethodInfo? _eventHandler;
        private static MethodInfo? _amplifySamples;
        private static MethodInfo? _attachSlider;

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("net.michaelripley.VoiceVolumeOverride");

            try
            {
                if (!PatchSettingsDialog(harmony))
                    return;
                if (!PatchProcessNewSamples(harmony))
                    return;
                if (PatchLocalDbInit(harmony))
                    Msg("Patching success!");
            }
            catch (TranspilerException e)
            {
                Error($"transpiler falure: {e.Message}");
            }
        }

        // returns false on error
        private static bool PatchSettingsDialog(Harmony harmony)
        {
            MethodInfo onAttachOriginal = AccessTools.DeclaredMethod(typeof(SettingsDialog), "OnAttach", new Type[] { });
            if (onAttachOriginal == null)
            {
                Error("VoiceVolumeOverride: could not find SettingsDialog.OnAttach() method");
                return false;
            }
            MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(VoiceVolumeOverride), nameof(VoiceVolumeOverride.SettingsDialogOnAttachTranspiler));
            _attachSlider = AccessTools.DeclaredMethod(typeof(VoiceVolumeOverride), nameof(AttachSlider));
            harmony.Patch(onAttachOriginal, transpiler: new HarmonyMethod(transpiler));
            return true;
        }

        private static IEnumerable<CodeInstruction> SettingsDialogOnAttachTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            // find type of local variable 0. This should be some weird generated SettingsDialog closure
            Type? localVariable0Type = null;
            for (int idx = 0; idx < codes.Count - 1; idx++)
            {
                if (OpCodes.Stloc_0.Equals(codes[idx + 1].opcode) && OpCodes.Newobj.Equals(codes[idx].opcode))
                {
                    ConstructorInfo constructor = (ConstructorInfo)codes[idx].operand;
                    localVariable0Type = constructor.DeclaringType;
                    Debug($"localVariable0 is of type {localVariable0Type.FullName}");
                    break;
                }
            }
            if (localVariable0Type == null)
            {
                throw new TranspilerException("Unable to find type of local variable 0 in SettingsDialog.OnAttach()");
            }

            // find the UIBuilder field inside the SettingsDialog closure
            FieldInfo uiField = AccessTools.DeclaredField(localVariable0Type, UIBUILDER_FIELD_NAME);
            if (uiField == null)
            {
                throw new TranspilerException("Unable to find UIBuilder field");
            }
            if (!uiField.FieldType.Equals(typeof(UIBuilder)))
            {
                throw new TranspilerException("ui field is not a UIBuilder");
            }

            CodeInstruction ret = codes[codes.Count - 1];
            if (!OpCodes.Ret.Equals(ret.opcode))
            {
                throw new TranspilerException("last instruction in SettingsDialog.OnAttach() was not a ret");
            }

            CodeInstruction[] newInstructions = new[] {
                new CodeInstruction(OpCodes.Ldloc_0),
                CodeInstruction.LoadField(localVariable0Type, UIBUILDER_FIELD_NAME),
                new CodeInstruction(OpCodes.Call, _attachSlider) // call our AttachSlider() function
            };

            codes.InsertRange(codes.Count - 1, newInstructions);

            return codes.AsEnumerable();
        }

        // a call to this is injected via a transpiler
        private static void AttachSlider(UIBuilder ui)
        {
            if (_volumeMultiplier == null)
            {
                Error("Volume multiplier setting hasn't been configured yet, and it should have been. The mod is broken now.");
                return;
            }

            // add a header
            ui.Text("<b>Mod: VoiceVolumeOverride</b>", alignment: new Alignment?());

            // set up the float field
            Sync<float> fieldValue = ui.HorizontalElementWithLabel("Microphone Volume Multiplier", 0.7f, () => ui.FloatField(0f, 100f, format: "F2")).ParsedValue;
            fieldValue.SyncWithSetting(VOICE_MULTIPLIER_SETTING_NAME, SettingSync.LocalChange.UpdateSetting);
            fieldValue.Value = _volumeMultiplier.Value;
            fieldValue.OnValueChange += s => _volumeMultiplier.Value = s.Value;

            // set up the slider
            Sync<float> sliderValue = ui.Slider(ui.Style.MinHeight, _volumeMultiplier.Value, 0f, 3f).Value;
            sliderValue.SyncWithSetting(VOICE_MULTIPLIER_SETTING_NAME, SettingSync.LocalChange.UpdateSetting);
            sliderValue.OnValueChange += s => _volumeMultiplier.Value = s.Value;
            Debug("Extra settings UI elements injected!");
        }

        // returns false on error
        private static bool PatchProcessNewSamples(Harmony harmony)
        {
            MethodInfo processNewSamplesOriginal = AccessTools.DeclaredMethod(typeof(AudioInput), "ProcessNewSamples", new Type[] { typeof(Span<StereoSample>) });
            if (processNewSamplesOriginal == null)
            {
                Error("VoiceVolumeOverride: could not find ProcessNewSamples(Span<StereoSample>) method");
                return false;
            }

            _eventHandler = AccessTools.DeclaredMethod(typeof(AudioSamplesHandler), "Invoke");
            if (_eventHandler == null)
            {
                Error("VoiceVolumeOverride: could not find AudioSamplesHandler.Invoke() method");
                return false;
            }

            MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(VoiceVolumeOverride), nameof(ProcessNewSamplesTranspiler));
            _amplifySamples = AccessTools.DeclaredMethod(typeof(VoiceVolumeOverride), nameof(AmplifySamples));
            harmony.Patch(processNewSamplesOriginal, transpiler: new HarmonyMethod(transpiler));
            return true;
        }

        // this injects a call to our AmplifySamples() function directly before the event handler callback that happens at the end of AudioInput.ProcessNewSamples
        private static IEnumerable<CodeInstruction> ProcessNewSamplesTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            for (int idx = 0; idx < codes.Count; idx++)
            {
                if (codes[idx].Calls(_eventHandler))
                {
                    CodeInstruction[] newInstructions = new[] {
                        new CodeInstruction(OpCodes.Dup), // the top of the stack is already the buffer we need to send to our static method, so we duplicate it
                        new CodeInstruction(OpCodes.Call, _amplifySamples) // call our AmplifySamples() function
                    };
                    codes.InsertRange(idx, newInstructions);
                    return codes.AsEnumerable();
                }
            }

            throw new TranspilerException("failed to find AmplifySamples() injection target");
        }

        // a call to this is injected via a transpiler
        private static void AmplifySamples(Span<StereoSample> buffer)
        {
            float mult = _volumeMultiplier == null ? 1f : _volumeMultiplier.Value;
            for (int idx = 0; idx < buffer.Length; idx++)
            {
                buffer[idx] *= mult;
            }
        }

        private static bool PatchLocalDbInit(Harmony harmony)
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(LocalDB), nameof(LocalDB.Initialize), new Type[] { });
            if (target == null)
            {
                Error("Could not find LocalDB.Initialize() method");
                return false;
            }
            MethodInfo loadSettings = AccessTools.DeclaredMethod(typeof(VoiceVolumeOverride), nameof(VoiceVolumeOverride.LoadSettings));
            harmony.Patch(target, postfix: new HarmonyMethod(loadSettings));
            return true;
        }

        // must be called to load the settings values from LocalDB
        private static void LoadSettings(Task __result)
        {
            __result.ContinueWith(antecedent =>
            {
                _volumeMultiplier = new LocalModeVariableProxy<float>(Engine.Current.LocalDB, VOICE_MULTIPLIER_SETTING_NAME, 1f);
                Msg("Loaded voice multiplier setting from localdb");
            });
        }

        private class TranspilerException : Exception
        {
            public TranspilerException(string message) : base(message) { }
        }
    }
}
