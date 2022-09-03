using CodeX;
using FrooxEngine;
using HarmonyLib;
using NeosModLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace VoiceVolumeOverride
{
    public class VoiceVolumeOverride : NeosMod
    {
        internal const string VERSION = "1.1.0";
        public override string Name => "VoiceVolumeOverride";
        public override string Author => "runtime";
        public override string Version => VERSION;
        public override string Link => "https://github.com/zkxs/VoiceVolumeOverride";

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<float> VOLUME_MULTIPLIER = new("volume_multiplier", "Voice volume multiplier. Must be between 0 and 100, inclusive.", () => 1.0f, false, (value) => value >= 0 && value <= 100);

        private static ModConfiguration? config = null;

        private static MethodInfo? _eventHandler;
        private static MethodInfo? _amplifySamples;

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("dev.zkxs.VoiceVolumeOverride");
            config = GetConfiguration();

            try
            {
                PatchProcessNewSamples(harmony);
            }
            catch (TranspilerException e)
            {
                Error($"transpiler falure: {e.Message}");
            }
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
            float mult = config!.GetValue(VOLUME_MULTIPLIER);
            for (int idx = 0; idx < buffer.Length; idx++)
            {
                buffer[idx] *= mult;
            }
        }

        private class TranspilerException : Exception
        {
            public TranspilerException(string message) : base(message) { }
        }
    }
}
