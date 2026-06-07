using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Turns an LLM storyboard spec (compact JSON) into a validated
/// <see cref="StoryboardSpec"/>, assembles the ready-to-paste video prompt for
/// each clip, renders the human-readable board + Facebook text for the Copy
/// buttons, and builds a ready ComfyUI scene workflow per clip.
///
/// Like <see cref="AiWorkflowBuilder"/>, parsing is defensive: markdown fences
/// and prose around the JSON are tolerated, and a malformed clip is skipped
/// rather than crashing the board — garbage in, a smaller-but-valid board out.
///
/// Expected JSON (the shape the LLM is told to emit in
/// <see cref="LlmService.WriteStoryboardAsync"/>):
///   { "title":"…",
///     "clips":[ { "title":"…", "durationSec":8, "visual":"…", "camera":"…",
///                 "sfx":"…", "audioBehavior":"…",
///                 "dialogue":[ {"text":"…","emotion":"…"} ],
///                 "cards":["The Star"] } ],
///     "facebook": { "caption":"…", "hashtags":["#…"], "commaTags":["…"] } }
/// </summary>
public static class StoryboardBuilder
{
    // ── Parse ────────────────────────────────────────────────────────────────
    public static StoryboardSpec Parse(string llmJson, StoryboardTemplate tpl)
    {
        var spec = new StoryboardSpec
        {
            Title = string.IsNullOrWhiteSpace(tpl.Name) ? "Storyboard" : tpl.Name,
            Concept = tpl.Concept,
            Character = tpl.Character,
            StyleNote = tpl.StyleNote,
            ReferenceTag = tpl.ReferenceTag,
            AspectId = tpl.AspectId,
            VoiceNote = tpl.VoiceNote,
        };

        var root = JsonNode.Parse(StripFences(llmJson)) as JsonObject
            ?? throw new InvalidOperationException("AI did not return a JSON object.");

        if (root["title"]?.GetValue<string>() is { Length: > 0 } title)
            spec.Title = title;
        if (root["concept"]?.GetValue<string>() is { Length: > 0 } concept)
            spec.Concept = concept;

        if (root["clips"] is JsonArray clips)
        {
            var index = 1;
            foreach (var item in clips)
            {
                if (item is not JsonObject c) continue;
                var clip = new StoryboardClip
                {
                    Index = index,
                    Title = Str(c, "title", $"คลิป {index}"),
                    DurationSec = Num(c, "durationSec", tpl.ClipDurationSec),
                    Visual = Str(c, "visual"),
                    Camera = Str(c, "camera"),
                    Sfx = Str(c, "sfx"),
                    AudioBehavior = Str(c, "audioBehavior", "audioBehaviour", "audio"),
                };
                if (c["dialogue"] is JsonArray dlg)
                {
                    foreach (var d in dlg)
                    {
                        if (d is JsonObject do_)
                        {
                            var text = Str(do_, "text", "line");
                            if (!string.IsNullOrWhiteSpace(text))
                                clip.Dialogue.Add(new DialogueLine(text.Trim(), Str(do_, "emotion")));
                        }
                        else if (d is JsonValue dv && dv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        {
                            clip.Dialogue.Add(new DialogueLine(s.Trim()));
                        }
                    }
                }
                if (c["cards"] is JsonArray cards)
                {
                    foreach (var card in cards)
                        if (card is JsonValue cv && cv.TryGetValue<string>(out var cs) && !string.IsNullOrWhiteSpace(cs))
                            clip.Cards.Add(cs.Trim());
                }

                // Skip a clip with nothing usable in it.
                if (string.IsNullOrWhiteSpace(clip.Visual) && clip.Dialogue.Count == 0) continue;

                spec.Clips.Add(clip);
                index++;
            }
        }

        if (spec.Clips.Count == 0)
            throw new InvalidOperationException("AI returned a storyboard with no usable clips.");

        if (root["facebook"] is JsonObject fb)
        {
            spec.Facebook.Caption = Str(fb, "caption", "text", "body");
            if (fb["hashtags"] is JsonArray tags)
                foreach (var t in tags)
                    if (t is JsonValue tv && tv.TryGetValue<string>(out var ts) && !string.IsNullOrWhiteSpace(ts))
                        spec.Facebook.Hashtags.Add(NormaliseHashtag(ts.Trim()));
            if (fb["commaTags"] is JsonArray ctags)
                foreach (var t in ctags)
                    if (t is JsonValue tv && tv.TryGetValue<string>(out var ts) && !string.IsNullOrWhiteSpace(ts))
                        spec.Facebook.CommaTags.Add(ts.Trim().TrimStart('#'));
            spec.Facebook.NotifyDerived();
        }

        return spec;
    }

    /// <summary>(Re)build every clip's assembled <see cref="StoryboardClip.VideoPrompt"/>
    /// from the board's character / style / voice. Called after parse and after
    /// the user edits the character so the prompts stay in sync.</summary>
    public static void RefreshVideoPrompts(StoryboardSpec spec)
    {
        foreach (var clip in spec.Clips)
            clip.VideoPrompt = BuildVideoPrompt(spec, clip);
    }

    /// <summary>Assemble the prompt actually sent to the talking-head engine.
    /// Mirrors the brand's reference clip block: locked character + reference
    /// image, scene, camera, SFX, then the spoken Thai dialogue and the tarot
    /// cards on screen.</summary>
    public static string BuildVideoPrompt(StoryboardSpec spec, StoryboardClip clip)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(spec.Character)) sb.Append(spec.Character.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(spec.StyleNote)) sb.Append(spec.StyleNote.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(spec.ReferenceTag))
            sb.Append("ใช้ภาพอ้างอิง ").Append(spec.ReferenceTag.Trim())
              .Append(" สำหรับใบหน้าและการแต่งกายของตัวละครให้คงเดิมทุกคลิป. ");

        if (!string.IsNullOrWhiteSpace(clip.Visual)) sb.Append(clip.Visual.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(clip.Camera)) sb.Append("Camera: ").Append(clip.Camera.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(clip.Sfx)) sb.Append("SFX: ").Append(clip.Sfx.Trim()).Append(". ");

        var lines = clip.Dialogue.Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList();
        if (lines.Count > 0)
        {
            sb.Append("spoken Thai dialogue, ").Append(spec.VoiceNote.Trim()).Append(":\n");
            foreach (var l in lines)
            {
                sb.Append('"').Append(l.Text.Trim().Trim('"')).Append('"');
                if (!string.IsNullOrWhiteSpace(l.Emotion)) sb.Append(" (").Append(l.Emotion.Trim()).Append(')');
                sb.Append('\n');
            }
        }

        var cards = clip.Cards.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (cards.Count > 0) sb.Append("Tarot cards on screen: ").Append(string.Join(", ", cards)).Append(". ");
        if (!string.IsNullOrWhiteSpace(clip.AudioBehavior))
            sb.Append("audio behavior: ").Append(clip.AudioBehavior.Trim()).Append(". ");

        sb.Append(AspectWord(spec.AspectId)).Append(", ").Append(clip.DurationLabel)
          .Append(", realistic cinematic, lip-sync to the Thai dialogue.");

        return sb.ToString().Trim();
    }

    // ── Copy-ready text renders ───────────────────────────────────────────────

    /// <summary>The whole board as a single pasteable script — concept hook, each
    /// clip block, then the Facebook post + hashtag + comma-tag sections, laid
    /// out the way the brand's reference document is.</summary>
    public static string RenderBoardText(StoryboardSpec spec)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(spec.Title)) sb.Append("◆ ").Append(spec.Title.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(spec.Concept)) sb.Append(spec.Concept.Trim()).Append("\n\n");

        foreach (var clip in spec.Clips)
        {
            sb.Append('[').Append(clip.Header).Append("]\n\n");
            if (!string.IsNullOrWhiteSpace(spec.Character)) sb.Append(spec.Character.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(spec.StyleNote)) sb.Append(spec.StyleNote.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(spec.ReferenceTag)) sb.Append("ใช้ภาพอ้างอิง ").Append(spec.ReferenceTag.Trim()).Append(" ทั้งหมด\n");
            if (!string.IsNullOrWhiteSpace(clip.Visual)) sb.Append('\n').Append(clip.Visual.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(clip.Camera)) sb.Append("กล้อง: ").Append(clip.Camera.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(clip.Sfx)) sb.Append("SFX: ").Append(clip.Sfx.Trim()).Append('\n');

            var lines = clip.Dialogue.Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList();
            if (lines.Count > 0)
            {
                sb.Append("\nspoken Thai dialogue:\n");
                foreach (var l in lines) sb.Append('"').Append(l.Text.Trim().Trim('"')).Append("\"\n");
            }

            var cards = clip.Cards.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (cards.Count > 0) sb.Append("\nไพ่:\n").Append(string.Join("\n", cards)).Append('\n');
            if (!string.IsNullOrWhiteSpace(clip.AudioBehavior)) sb.Append("\naudio behavior: ").Append(clip.AudioBehavior.Trim()).Append('\n');
            sb.Append('\n');
        }

        sb.Append(RenderFacebookText(spec.Facebook));
        return sb.ToString().Trim();
    }

    /// <summary>The Facebook caption + hashtag block + comma-tag block.</summary>
    public static string RenderFacebookText(FacebookPost fb)
    {
        var sb = new StringBuilder();
        sb.Append("คำโพสต์ Facebook\n\n");
        if (!string.IsNullOrWhiteSpace(fb.Caption)) sb.Append(fb.Caption.Trim()).Append("\n\n");
        if (fb.Hashtags.Count > 0) sb.Append(fb.HashtagLine).Append("\n\n");
        if (fb.CommaTags.Count > 0) sb.Append("แท็กแบบคอมม่า\n\n").Append(fb.CommaTagLine);
        return sb.ToString().Trim();
    }

    // ── ComfyUI scene workflow ────────────────────────────────────────────────

    /// <summary>
    /// Build a ready, valid ComfyUI text-to-image workflow for the clip's SCENE
    /// (character + visual + style — no dialogue, since a still can't speak). The
    /// graph is the canonical SDXL path: checkpoint → CLIP(+/−) → latent →
    /// sampler → VAE decode → save, all wired with the real socket ids
    /// <see cref="NodeKinds"/> seeds, so it round-trips cleanly through
    /// <see cref="NodeFlowConverter"/> and runs on the user's ComfyUI server.
    /// </summary>
    public static FlowGraph BuildComfyGraph(StoryboardSpec spec, StoryboardClip clip)
    {
        var (w, h) = AspectToSize(spec.AspectId);
        var positive = string.Join(", ", new[]
        {
            spec.Character, clip.Visual, spec.StyleNote, "cinematic lighting, highly detailed, sharp focus",
        }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

        var graph = new FlowGraph();

        var ckpt = Node("ckpt", NodeKind.LoadCheckpoint, 60, 60);
        var pos  = Node("pos", NodeKind.CLIPTextEncode, 360, 40);
        var neg  = Node("neg", NodeKind.CLIPTextEncode, 360, 280);
        var lat  = Node("latent", NodeKind.EmptyLatentImage, 360, 520);
        var ks   = Node("ksampler", NodeKind.KSampler, 680, 200);
        var vae  = Node("vae", NodeKind.VAEDecode, 1000, 200);
        var save = Node("save", NodeKind.SaveImage, 1300, 200);
        foreach (var n in new[] { ckpt, pos, neg, lat, ks, vae, save }) graph.Nodes.Add(n);

        // Leave ckpt_name empty — the ComfyUI submit path auto-picks an installed
        // checkpoint (AutoFixModelReferencesAsync), so the export isn't pinned to a
        // model the user's server may not have.
        SetParam(ckpt, "ckpt_name", "");
        SetParam(pos, "text", positive);
        SetParam(neg, "text", "blurry, low quality, watermark, text, deformed, bad anatomy, extra fingers");
        SetParam(lat, "width", w.ToString());
        SetParam(lat, "height", h.ToString());
        SetParam(save, "filename_prefix", $"chanthra/storyboard-clip{clip.Index}");

        Wire(graph, ckpt, "clip", pos, "clip");
        Wire(graph, ckpt, "clip", neg, "clip");
        Wire(graph, ckpt, "model", ks, "model");
        Wire(graph, pos, "cond", ks, "positive");
        Wire(graph, neg, "cond", ks, "negative");
        Wire(graph, lat, "latent", ks, "latent_image");
        Wire(graph, ks, "latent", vae, "samples");
        Wire(graph, ckpt, "vae", vae, "vae");
        Wire(graph, vae, "image", save, "images");

        return graph;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static FlowNode Node(string id, NodeKind kind, double x, double y)
    {
        var n = new FlowNode
        {
            Id = id,
            Kind = kind,
            Title = NodeKinds.Humanise(kind),
            AccentKey = NodeKinds.Accent(kind),
            X = x,
            Y = y,
            Width = 230,
        };
        NodeKinds.SeedSockets(n, kind);
        return n;
    }

    private static void SetParam(FlowNode n, string label, string value)
    {
        var existing = n.Params.FirstOrDefault(p => p.Label == label);
        if (existing is not null) existing.Value = value;
        else n.Params.Add(new NodeParam { Label = label, Value = value });
    }

    private static void Wire(FlowGraph g, FlowNode from, string fromSock, FlowNode to, string toSock)
    {
        var outSocket = from.Outputs.FirstOrDefault(s => s.Id == fromSock);
        var inSocket = to.Inputs.FirstOrDefault(s => s.Id == toSock);
        if (outSocket is null || inSocket is null) return;
        g.Wires.Add(new FlowWire
        {
            Id = $"{from.Id}.{fromSock}->{to.Id}.{toSock}",
            FromNodeId = from.Id,
            FromSocketId = fromSock,
            ToNodeId = to.Id,
            ToSocketId = toSock,
            Type = outSocket.Type,
        });
    }

    public static (int W, int H) AspectToSize(string aspectId) => aspectId switch
    {
        "9:16" => (832, 1216),
        "1:1" => (1024, 1024),
        "21:9" => (1280, 544),
        _ => (1216, 832),
    };

    public static AspectRatio AspectToEnum(string aspectId) => aspectId switch
    {
        "9:16" => AspectRatio.Vertical,
        "1:1" => AspectRatio.Square,
        "21:9" => AspectRatio.Cinema,
        _ => AspectRatio.Wide,
    };

    private static string AspectWord(string aspectId) => aspectId switch
    {
        "9:16" => "9:16 vertical",
        "1:1" => "1:1 square",
        "21:9" => "21:9 cinema",
        _ => "16:9 widescreen",
    };

    private static string NormaliseHashtag(string s) => s.StartsWith('#') ? s : "#" + s.Replace(" ", "");

    private static string Str(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
            if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        return "";
    }

    private static double Num(JsonObject o, string key, double fallback)
    {
        if (o[key] is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var ds)) return ds;
        }
        return fallback;
    }

    /// <summary>Take the substring between the first '{' and the last '}' so
    /// markdown fences / "Here is your storyboard:" preambles don't break the parse.</summary>
    private static string StripFences(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var s = raw.Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start) return s[start..(end + 1)];
        return s;
    }
}
