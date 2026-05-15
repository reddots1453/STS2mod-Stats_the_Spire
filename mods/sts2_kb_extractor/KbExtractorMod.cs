using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace KbExtractor;

[ModInitializerAttribute("Initialize")]
public static class KbExtractorMod
{
    private static bool _f6Pressed;

    public static void Initialize()
    {
        GD.Print("[KBExtractor] Knowledge Base Extractor loaded. Press F6 to extract.");
        Task.Run(async () =>
        {
            try
            {
                while (Engine.GetMainLoop() is not SceneTree st || st.Root == null)
                    await Task.Delay(200);
                ((SceneTree)Engine.GetMainLoop()).ProcessFrame += OnFrame;
            }
            catch (Exception ex) { GD.PrintErr($"[KBExtractor] Init error: {ex}"); }
        });
    }

    private static void OnFrame()
    {
        try
        {
            bool f6 = Input.IsKeyPressed(Key.F6);
            if (f6 && !_f6Pressed)
            {
                GD.Print("[KBExtractor] Extraction started (game may freeze briefly)...");
                try { Extractor.Run(); }
                catch (Exception ex) { GD.PrintErr($"[KBExtractor] Extraction failed: {ex}"); }
            }
            _f6Pressed = f6;
        }
        catch { }
    }
}
