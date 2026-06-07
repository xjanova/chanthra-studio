using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Single source of truth for a <see cref="NodeKind"/>'s socket layout, display
/// title, and header accent. Shared by the Node Flow editor (manual add) and
/// the AI workflow builder so a node always gets the SAME, valid sockets — the
/// socket ids ARE the ComfyUI input keys, so NodeFlowConverter can emit valid
/// wire targets from either path.
/// </summary>
public static class NodeKinds
{
    /// <summary>Populate <paramref name="n"/> with the canonical sockets +
    /// default params for its <see cref="NodeKind"/>.</summary>
    public static void SeedSockets(FlowNode n, NodeKind kind)
    {
        switch (kind)
        {
            case NodeKind.LoadCheckpoint:
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "clip",  Label = "CLIP",  Type = SocketType.Clip,  Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "vae",   Label = "VAE",   Type = SocketType.Vae,   Row = 2 });
                n.Params.Add(new NodeParam { Label = "ckpt_name", Value = "v1-5-pruned-emaonly.safetensors" });
                break;
            case NodeKind.CLIPTextEncode:
                n.Inputs.Add(new NodeSocket { Id = "clip", Label = "clip", Type = SocketType.Clip, IsInput = true, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "cond", Label = "CONDITIONING", Type = SocketType.Conditioning, Row = 0 });
                n.Params.Add(new NodeParam { Label = "text", Value = "", Editor = "textarea" });
                break;
            case NodeKind.KSampler:
                n.Inputs.Add(new NodeSocket { Id = "model",    Label = "model",        Type = SocketType.Model,        IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "positive", Label = "positive",     Type = SocketType.Conditioning, IsInput = true, Row = 1 });
                n.Inputs.Add(new NodeSocket { Id = "negative", Label = "negative",     Type = SocketType.Conditioning, IsInput = true, Row = 2 });
                n.Inputs.Add(new NodeSocket { Id = "latent_image", Label = "latent_image", Type = SocketType.Latent,   IsInput = true, Row = 3 });
                n.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, Row = 0 });
                n.Params.Add(new NodeParam { Label = "seed", Value = "0" });
                n.Params.Add(new NodeParam { Label = "steps", Value = "28" });
                n.Params.Add(new NodeParam { Label = "cfg", Value = "7.5" });
                n.Params.Add(new NodeParam { Label = "sampler_name", Value = "dpmpp_2m" });
                n.Params.Add(new NodeParam { Label = "scheduler", Value = "karras" });
                n.Params.Add(new NodeParam { Label = "denoise", Value = "1.0" });
                break;
            case NodeKind.VAEDecode:
                n.Inputs.Add(new NodeSocket { Id = "samples", Label = "samples", Type = SocketType.Latent, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "vae",     Label = "vae",     Type = SocketType.Vae,    IsInput = true, Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "image", Label = "IMAGE", Type = SocketType.Image, Row = 0 });
                break;
            case NodeKind.SaveImage:
                n.Inputs.Add(new NodeSocket { Id = "images", Label = "images", Type = SocketType.Image, IsInput = true, Row = 0 });
                n.Params.Add(new NodeParam { Label = "filename_prefix", Value = "chanthra" });
                break;
            case NodeKind.EmptyLatentImage:
                n.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, Row = 0 });
                n.Params.Add(new NodeParam { Label = "width", Value = "1024" });
                n.Params.Add(new NodeParam { Label = "height", Value = "1024" });
                n.Params.Add(new NodeParam { Label = "batch_size", Value = "1" });
                break;
            case NodeKind.LoadImage:
                n.Outputs.Add(new NodeSocket { Id = "image", Label = "IMAGE", Type = SocketType.Image, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "mask",  Label = "MASK",  Type = SocketType.Image, Row = 1 });
                n.Params.Add(new NodeParam { Label = "image", Value = "reference.png" });
                break;
            case NodeKind.LoraLoader:
                n.Inputs.Add(new NodeSocket { Id = "model", Label = "model", Type = SocketType.Model, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "clip",  Label = "clip",  Type = SocketType.Clip,  IsInput = true, Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "clip",  Label = "CLIP",  Type = SocketType.Clip,  Row = 1 });
                n.Params.Add(new NodeParam { Label = "lora_name", Value = "" });
                n.Params.Add(new NodeParam { Label = "strength_model", Value = "1.0" });
                n.Params.Add(new NodeParam { Label = "strength_clip",  Value = "1.0" });
                break;
            case NodeKind.ControlNetApply:
                n.Inputs.Add(new NodeSocket { Id = "conditioning", Label = "conditioning", Type = SocketType.Conditioning, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "control_net",  Label = "control_net",  Type = SocketType.Model,        IsInput = true, Row = 1 });
                n.Inputs.Add(new NodeSocket { Id = "image",        Label = "image",        Type = SocketType.Image,        IsInput = true, Row = 2 });
                n.Outputs.Add(new NodeSocket { Id = "conditioning", Label = "CONDITIONING", Type = SocketType.Conditioning, Row = 0 });
                n.Params.Add(new NodeParam { Label = "strength", Value = "1.0" });
                break;
            case NodeKind.AnimateDiff:
                n.Inputs.Add(new NodeSocket { Id = "model", Label = "model", Type = SocketType.Model, IsInput = true, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Params.Add(new NodeParam { Label = "motion_model", Value = "" });
                n.Params.Add(new NodeParam { Label = "beta_schedule", Value = "autoselect" });
                break;
        }
    }

    public static string Humanise(NodeKind k) => k switch
    {
        NodeKind.LoadCheckpoint  => "Load Checkpoint",
        NodeKind.CLIPTextEncode  => "CLIP Text Encode",
        NodeKind.KSampler        => "K Sampler",
        NodeKind.VAEDecode       => "VAE Decode",
        NodeKind.SaveImage       => "Save Image",
        NodeKind.EmptyLatentImage => "Empty Latent Image",
        NodeKind.LoadImage       => "Load Image",
        NodeKind.LoraLoader      => "Load LoRA",
        NodeKind.ControlNetApply => "ControlNet Apply",
        NodeKind.AnimateDiff     => "AnimateDiff",
        _ => k.ToString(),
    };

    public static string Accent(NodeKind k) => k switch
    {
        NodeKind.LoadCheckpoint or NodeKind.KSampler or NodeKind.LoraLoader => "BrushClipGold",
        NodeKind.CLIPTextEncode or NodeKind.VAEDecode                       => "BrushClipAmber",
        NodeKind.EmptyLatentImage or NodeKind.SaveImage                     => "BrushClipPlum",
        _ => "BrushClipPlum",
    };
}
