using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CommunityStats.Util;

/// <summary>
/// Lazy cache of native game intent textures used by the Round 9 round 4
/// IntentStateMachinePanel rewrite (PRD §3.10).
///
/// **Round 9 round 5 fix — disposed-texture trap:**
/// The first version of this cache stored `Texture2D` instances directly.
/// Godot reclaims `AtlasTexture` instances on scene transitions /
/// resource cleanup, leaving the cached references as disposed C# wrappers.
/// The next read failed with `ObjectDisposedException: Godot.AtlasTexture`
/// inside `TextureRect.Texture` setter (visible in the godot.log around the
/// "Cannot access a disposed object" warnings logged from `IntentHover`).
///
/// **Fix**: cache the resolved resource path (`string`), NOT the texture
/// instance. Each `GetIcon` call re-loads via `ResourceLoader.Load&lt;Texture2D&gt;`
/// with `CacheMode.Reuse` — Godot's own resource cache returns the same
/// instance if it's still alive, or a fresh one if it was reclaimed.
///
/// Strategy:
/// 1. For non-AttackIntent types, read the protected `SpritePath` field via
///    Traverse. Every concrete subclass overrides it with a constant `.tres`
///    path under `atlases/intent_atlas.sprites/intent_*.tres`.
/// 2. For `AttackIntent` and its subclasses, replicate the in-engine tier
///    selection (`damage &lt; 5 → attack_1`, &lt; 10 → 2, &lt; 20 → 3, &lt; 40 → 4,
///    else 5) and load the matching `attack/intent_attack_N.tres`.
/// 3. Cache resolved path by `(System.Type, damageTier)` so repeated lookups
///    skip the reflection step.
/// </summary>
public static class IntentIconCache
{
    private static readonly Dictionary<(Type, int), string?> _pathCache = new();

    /// <summary>
    /// Get the icon texture for a given intent type. For attack intents,
    /// `damageHint` selects the attack tier (1-5); pass the base single-hit
    /// damage value. For non-attack intents `damageHint` is ignored.
    /// Returns null if the texture can't be resolved (e.g. missing asset).
    ///
    /// Always re-loads via ResourceLoader so stale Godot wrapper instances
    /// (disposed by scene transitions) don't poison the cache.
    /// </summary>
    public static Texture2D? GetIcon(AbstractIntent intent, int damageHint = 0)
    {
        if (intent == null) return null;
        var resPath = ResolvePath(intent, damageHint);
        if (string.IsNullOrEmpty(resPath)) return null;

        try
        {
            if (!Godot.ResourceLoader.Exists(resPath)) return null;
            var tex = Godot.ResourceLoader.Load<Texture2D>(
                resPath, null, Godot.ResourceLoader.CacheMode.Reuse);
            if (tex == null) return null;
            // Validate the wrapper isn't already disposed (Godot's resource
            // cache occasionally hands out stale handles that throw on the
            // very next property read).
            if (!Godot.GodotObject.IsInstanceValid(tex)) return null;
            try { _ = tex.GetSize(); } // probe — throws if disposed
            catch (ObjectDisposedException) { return null; }
            return tex;
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentIconCache] failed to load {resPath}: {ex.Message}");
            return null;
        }
    }

    private static string? ResolvePath(AbstractIntent intent, int damageHint)
    {
        var t = intent.GetType();
        int tier = 0;
        if (intent is AttackIntent)
        {
            tier = AttackTier(damageHint);
        }
        var key = (t, tier);
        if (_pathCache.TryGetValue(key, out var cached)) return cached;

        string? resPath = null;
        try
        {
            if (intent is AttackIntent)
            {
                resPath = ImageHelper.GetImagePath(
                    "atlases/intent_atlas.sprites/attack/intent_attack_" + tier + ".tres");
            }
            else
            {
                // Read the protected `SpritePath` instance property via Traverse.
                var sprite = Traverse.Create(intent).Property("SpritePath").GetValue<string>();
                if (string.IsNullOrEmpty(sprite)) sprite = null;
                resPath = sprite != null ? ImageHelper.GetImagePath(sprite) : null;
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentIconCache] failed to resolve path for {t.Name}: {ex.Message}");
        }
        _pathCache[key] = resPath;
        return resPath;
    }

    /// <summary>
    /// Replicate the in-engine attack tier selection.
    /// `damage &lt; 5 → 1`, `&lt; 10 → 2`, `&lt; 20 → 3`, `&lt; 40 → 4`, `else 5`.
    /// </summary>
    private static int AttackTier(int damage)
    {
        if (damage < 5) return 1;
        if (damage < 10) return 2;
        if (damage < 20) return 3;
        if (damage < 40) return 4;
        return 5;
    }
}
