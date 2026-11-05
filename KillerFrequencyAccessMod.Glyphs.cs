using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using SpriteMarkup = Il2CppTeam17.UI.Input.SpriteInputBindingMarkupObject;

namespace Killer_Frequency_Access;

/// <summary>
/// Button glyphs in game text. The game writes a glyph as a TextMeshPro sprite tag. Which control a
/// glyph stands for comes from the game's SpriteInputBindingMarkupObject assets
/// (InputMarkupDefinition_XB1, _DS4, _DS5, _NX, _Keyboard, _Oculus in the rip): each names its sprite
/// atlas (m_SpriteAsset) and maps bindings ("buttonSouth", "dpad/up") to sprite ids. The control is
/// spoken with the active device's own name for it, as for action keys.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private static readonly Regex SpriteTag = new("<sprite[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpriteAtlas = new("sprite\\s*=\\s*\"?([^\"\\s>]+)\"?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpriteIndex = new("index\\s*=\\s*\"?(\\d+)\"?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpriteName = new("name\\s*=\\s*\"?([^\"\\s>]+)\"?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>(atlas name, sprite id) to binding, from the game's markup assets.</summary>
    private static Dictionary<(string Atlas, int Id), (string Binding, bool Keyboard)>? _glyphBindings;

    /// <summary>Replaces each sprite tag with the control it shows; other tags are left for the caller to strip.</summary>
    private static string SpeakGlyphs(string text)
    {
        if (text.IndexOf("<sprite", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return text;
        }

        return SpriteTag.Replace(text, match => " " + DescribeGlyph(match.Value) + " ");
    }

    private static string DescribeGlyph(string tag)
    {
        var map = GetGlyphBindings();
        var atlasMatch = SpriteAtlas.Match(tag);
        var atlas = atlasMatch.Success ? atlasMatch.Groups[1].Value : string.Empty;
        var id = -1;
        var indexMatch = SpriteIndex.Match(tag);
        if (indexMatch.Success)
        {
            id = int.Parse(indexMatch.Groups[1].Value);
        }
        else
        {
            // A sprite named "ControllerLayout_XB1_12" is id 12 of atlas ControllerLayout_XB1 (rip).
            var nameMatch = SpriteName.Match(tag);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value : string.Empty;
            var underscore = name.LastIndexOf('_');
            if (underscore > 0 && int.TryParse(name[(underscore + 1)..], out var parsed))
            {
                id = parsed;
                if (atlas.Length == 0)
                {
                    atlas = name[..underscore];
                }
            }
        }

        if (map != null && map.TryGetValue((atlas, id), out var entry))
        {
            return DescribeGlyphBinding(entry.Item1, entry.Item2);
        }

        LogOnce($"glyph-unknown|{tag}", $"[glyphs] no markup asset maps {tag} (atlas \"{atlas}\", id {id})");
        return "button";
    }

    /// <summary>The binding spoken with the active gamepad's control name, or as a key for the keyboard atlas.</summary>
    private static string DescribeGlyphBinding(string binding, bool keyboard)
    {
        if (!keyboard)
        {
            var device = GetActiveInputDevice();
            if (device != null && !(device is Keyboard) && !(device is Mouse))
            {
                var control = device.TryGetChildControl(binding);
                if (control != null && !string.IsNullOrWhiteSpace(control.displayName))
                {
                    return control.displayName;
                }
            }

            return SplitWords(binding.Replace('/', ' '));
        }

        return DescribeControlPath(binding.StartsWith("<", StringComparison.Ordinal) ? binding : $"<Keyboard>/{binding}");
    }

    private static Dictionary<(string, int), (string, bool)>? GetGlyphBindings()
    {
        if (_glyphBindings != null)
        {
            return _glyphBindings;
        }

        var map = new Dictionary<(string, int), (string, bool)>();
        foreach (var markup in Resources.FindObjectsOfTypeAll<SpriteMarkup>())
        {
            if (markup == null || markup.m_SpriteAsset == null || markup.m_DeviceBindingSprites == null)
            {
                continue;
            }

            var atlas = markup.m_SpriteAsset.name;
            var keyboard = markup.name.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0;
            var pairs = markup.m_DeviceBindingSprites;
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                if (pair != null && !map.ContainsKey((atlas, pair.spriteId)))
                {
                    map[(atlas, pair.spriteId)] = (pair.binding, keyboard);
                }
            }
        }

        if (map.Count == 0)
        {
            LogOnce("glyph-no-assets", "[glyphs] no SpriteInputBindingMarkupObject assets are loaded yet");
            return null;
        }

        MelonLoader.MelonLogger.Msg($"[glyphs] {map.Count} glyph sprites mapped to bindings");
        _glyphBindings = map;
        return _glyphBindings;
    }
}
