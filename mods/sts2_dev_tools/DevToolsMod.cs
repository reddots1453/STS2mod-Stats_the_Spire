using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace DevTools;

[ModInitializerAttribute("Initialize")]
public static class DevToolsMod
{
    private static bool _f7Pressed;
    private static DevToolsPanel? _panel;

    public static void Initialize()
    {
        GD.Print("[DevTools] Dev Tools mod initializing...");

        try
        {
            RegisterHotkey();
            GD.Print("[DevTools] Initialized. Press F7 to open menu.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DevTools] Init failed: {ex}");
        }
    }

    private static void RegisterHotkey()
    {
        Task.Run(async () =>
        {
            try
            {
                while (Engine.GetMainLoop() is not SceneTree sceneTree || sceneTree.Root == null)
                    await Task.Delay(200);

                var tree = (SceneTree)Engine.GetMainLoop();
                tree.ProcessFrame += OnProcessFrame;

                _panel = new DevToolsPanel();
                tree.Root.CallDeferred(Node.MethodName.AddChild, _panel);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[DevTools] Hotkey setup failed: {ex}");
            }
        });
    }

    private static void OnProcessFrame()
    {
        try
        {
            bool f7Now = Input.IsKeyPressed(Key.F7);
            if (f7Now && !_f7Pressed)
                _panel?.Toggle();
            _f7Pressed = f7Now;
        }
        catch { }
    }
}
