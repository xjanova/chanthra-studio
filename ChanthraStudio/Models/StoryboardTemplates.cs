using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

/// <summary>
/// A reusable storyboard recipe. Selecting one fills the concept form; the
/// user can then edit and press Generate to let the LLM expand it into clips.
///
/// Template #1 (<c>fortune-money</c>) additionally ships a fully-built
/// <see cref="BuildExample"/> board so the screen is populated with a real,
/// ready-to-use storyboard the moment it's picked — no LLM call required. This
/// is the "เทมเพลตสำเร็จพร้อมใช้" the brand asked for.
/// </summary>
public sealed class StoryboardTemplate : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Blurb { get; init; } = "";
    public string Concept { get; init; } = "";
    public string Character { get; init; } = "";
    public string StyleNote { get; init; } = "realistic cinematic style";
    public string ReferenceTag { get; init; } = "@ref1";
    public string AspectId { get; init; } = "9:16";
    public int ClipCount { get; init; } = 4;
    public double ClipDurationSec { get; init; } = 8;
    public string VoiceNote { get; init; } = "sweet young Thai female voice age 18-22, cheerful";

    /// <summary>Builds a complete example board (template #1 only). Null = the
    /// user generates from the concept via the LLM.</summary>
    public Func<StoryboardSpec>? BuildExample { get; init; }

    public bool HasExample => BuildExample is not null;

    private bool _isActive;
    /// <summary>True when this is the picked template — drives the gold border
    /// in the gallery.</summary>
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

public static class StoryboardTemplates
{
    public static IReadOnlyList<StoryboardTemplate> All { get; } = new[]
    {
        new StoryboardTemplate
        {
            Id = "fortune-money",
            Name = "เลิกจน · สัญญาณความรวย",
            Blurb = "แม่หมอเปิดไพ่ · ฟ้าส่งสัญญาณ · พิมพ์ \"เลิกจน\" แล้วแชร์",
            Concept =
                "ฟ้าส่งสัญญาณมาบอกแล้ว โอกาสรวยมาแล้ว ของคนมีตัว M และมีกากบาทกลางฝ่ามือ " +
                "มันเข้ามาแล้ว แต่โดนปิดบังด้วยเคราะห์กรรมบางอย่างที่ต้องจ่ายก่อน เพราะเป็นกรรม" +
                "ติดตัวของผู้มีเทพคุ้มครอง แต่ละคนไม่เหมือนกัน พิมพ์ \"เลิกจน\" แล้วแชร์ออกไป " +
                "เพื่อให้ฟ้ารับรู้ว่าคุณพร้อมในความรวยนั้นแล้ว",
            Character = "บุษบา แม่หมอสาวผมขาว ยิ้มสดใสตลอดเวลา, ห้องพยากรณ์สว่างอบอุ่น, ไพ่ยิปซีเรืองแสง",
            StyleNote = "realistic cinematic style, warm golden light",
            ReferenceTag = "@ref1",
            AspectId = "9:16",
            ClipCount = 4,
            ClipDurationSec = 8,
            VoiceNote = "cute young Thai female voice age 18-22, cheerful, spoken dialogue only",
            BuildExample = BuildFortuneMoneyExample,
        },
        new StoryboardTemplate
        {
            Id = "fortune-love",
            Name = "ดวงความรัก · เนื้อคู่",
            Blurb = "เปิดไพ่เรื่องหัวใจ · คนที่ใช่กำลังเข้ามา",
            Concept =
                "ไพ่บอกว่าความรักครั้งใหม่กำลังจะเข้ามาหาคนที่เคยผิดหวัง คนที่ใช่กำลังเดินทางมา " +
                "แต่ยังมีบางอย่างในใจที่ต้องปล่อยวางก่อน พิมพ์ \"เปิดใจ\" แล้วแชร์ เพื่อเปิดทางให้รักครั้งใหม่",
            Character = "บุษบา แม่หมอสาวผมขาว ยิ้มอ่อนโยน, ห้องพยากรณ์โทนชมพูมงคล, ไพ่ยิปซีเรืองแสง",
            StyleNote = "realistic cinematic style, soft rosy light",
            ClipCount = 4,
            ClipDurationSec = 8,
            VoiceNote = "warm young Thai female voice age 18-22, gentle and sincere",
        },
        new StoryboardTemplate
        {
            Id = "fortune-daily",
            Name = "ดวงวันนี้ · เลขมงคล",
            Blurb = "ทำนายดวงรายวัน + เลขนำโชค 3 ช่วง",
            Concept =
                "ดวงประจำวันนี้กำลังเปลี่ยน มีจังหวะดีเข้ามาสำหรับคนที่พร้อม พร้อมเลขมงคลนำโชค " +
                "พิมพ์ \"รับทรัพย์\" แล้วแชร์ เพื่อเรียกโชคลาภเข้าหาตัวเอง",
            Character = "บุษบา แม่หมอสาวผมขาว ยิ้มสดใส, ห้องพยากรณ์สว่างอบอุ่น",
            StyleNote = "realistic cinematic style, warm light",
            ClipCount = 3,
            ClipDurationSec = 8,
            VoiceNote = "cheerful young Thai female voice age 18-22",
        },
        new StoryboardTemplate
        {
            Id = "freeform",
            Name = "อิสระ · เขียนคอนเซ็ปต์เอง",
            Blurb = "พิมพ์คอนเซ็ปต์เอง แล้วให้ AI ขยายเป็นสตอรี่บอร์ด",
            Concept = "",
            Character = "บุษบา แม่หมอสาวผมขาว ยิ้มสดใส, ห้องพยากรณ์อบอุ่น, ไพ่ยิปซีเรืองแสง",
            StyleNote = "realistic cinematic style",
            ClipCount = 4,
            ClipDurationSec = 8,
        },
    };

    public static StoryboardTemplate? FindById(string id) =>
        All.FirstOrDefault(t => t.Id == id);

    // ── The seeded example — a 1:1 reproduction of the brand's reference board ──
    private static StoryboardSpec BuildFortuneMoneyExample()
    {
        var tpl = All[0];
        var spec = new StoryboardSpec
        {
            Title = "เลิกจน · ฟ้าส่งสัญญาณความรวย",
            Concept = tpl.Concept,
            Character = tpl.Character,
            StyleNote = tpl.StyleNote,
            ReferenceTag = tpl.ReferenceTag,
            AspectId = tpl.AspectId,
            VoiceNote = tpl.VoiceNote,
        };

        spec.Clips.Add(Clip(1, "ฟ้าส่งสัญญาณมาแล้ว",
            visual: "เปิดคลิปด้วยเสียงกระดิ่งและแสงสีทองพุ่งออกจากไพ่ บุษบาเงยหน้าขึ้นมองกล้องด้วยรอยยิ้มสดใส",
            camera: "กล้องซูมเร็วเข้าที่ใบหน้าบุษบา (fast zoom-in)",
            sfx: "\"กริ๊งงง!\" เสียงกระดิ่ง + แสงสีทองพุ่งจากไพ่",
            audio: "cute young Thai female voice, cheerful, spoken dialogue only",
            cards: new[] { "The Star", "The Sun" },
            lines: new[] { "หยุดก่อนค่ะ!", "ฟ้าส่งสัญญาณมาบอกแล้ว", "โอกาสรวยของคุณ... กำลังเข้ามาแล้วค่ะ" }));

        spec.Clips.Add(Clip(2, "คนมีตัว M และกากบาทกลางฝ่ามือ",
            visual: "บุษบาเปิดไพ่บนโต๊ะพร้อมรอยยิ้ม แสงทองวาบที่ไพ่",
            camera: "close-up ไพ่บนโต๊ะ",
            sfx: "เสียงเปิดไพ่ + ประกายแสงเบา ๆ",
            audio: "friendly storytelling, youthful voice",
            cards: new[] { "Wheel of Fortune", "Ace of Pentacles" },
            lines: new[]
            {
                "โดยเฉพาะคนที่เชื่อว่าตัวเองมีตัวเอ็ม",
                "หรือมีกากบาทกลางฝ่ามือ",
                "เหมือนมีบางอย่างกำลังผลักดันชีวิตให้ก้าวไปข้างหน้า",
            }));

        spec.Clips.Add(Clip(3, "สิ่งที่ขวางอยู่",
            visual: "แสงในห้องหรี่ลงเล็กน้อย บุษบาค่อย ๆ เปิดไพ่ใบถัดไปอย่างตั้งใจ",
            camera: "slow reveal, medium close-up",
            sfx: "เสียงบรรยากาศลึกลับ ลมพัดเบา ๆ",
            audio: "calm, mysterious, thoughtful",
            cards: new[] { "Justice", "The Hierophant" },
            lines: new[]
            {
                "แต่ทำไมยังไม่ถึงจุดที่หวัง?",
                "ไพ่บอกว่า... ยังมีภาระบางอย่าง",
                "บทเรียนบางอย่าง",
                "ที่แต่ละคนต้องผ่านไม่เหมือนกัน",
            }));

        spec.Clips.Add(Clip(4, "ประกาศความพร้อม",
            visual: "แสงสีทองกลับมาสว่างขึ้น บุษบายิ้มให้กล้อง ตอนท้ายยกไพ่ขึ้นมายิ้มอย่างมีความหวัง",
            camera: "push-in ปิดท้ายช้า ๆ",
            sfx: "เสียงประกายแสงมงคล",
            audio: "sweet young female voice, hopeful ending, no singing",
            cards: new[] { "The World", "Ten of Pentacles" },
            lines: new[]
            {
                "ถ้าคุณพร้อมเปิดรับโอกาสใหม่",
                "พิมพ์ เลิกจน",
                "แล้วแชร์ออกไป",
                "เพื่อประกาศกับตัวเองว่า",
                "คุณพร้อมเดินหน้าสู่ความสำเร็จแล้วค่ะ",
                "โชคดีนะคะ",
            }));

        spec.Facebook.Caption =
            "✨ ฟ้ากำลังส่งสัญญาณบางอย่างมาถึงคุณ...\n\n" +
            "เคยรู้สึกไหมว่า\nชีวิตเหมือนกำลังจะดีขึ้น\nแต่ยังมีบางอย่างคอยฉุดรั้งเอาไว้\n\n" +
            "🔮 วันนี้บุษบาเปิดไพ่แล้วพบว่า\nหลายคนกำลังอยู่ในช่วงเปลี่ยนผ่านครั้งสำคัญ\n\n" +
            "โอกาสใหม่\nความสำเร็จ\nและการเติบโต\n\nอาจอยู่ใกล้กว่าที่คิด\n\n" +
            "แต่ก่อนจะไปถึงจุดนั้น\nแต่ละคนก็ต้องผ่านบทเรียนของตัวเองเสียก่อน\n\n" +
            "หากคุณพร้อมเปิดรับสิ่งใหม่ ๆ\n\n💬 พิมพ์ \"เลิกจน\"\n\nแล้วแชร์โพสต์นี้ออกไป\n\n" +
            "เพื่อเป็นการประกาศเป้าหมายกับตัวเอง\nและเริ่มต้นก้าวต่อไปด้วยความตั้งใจ ✨";

        foreach (var h in new[]
        {
            "#บุษบาพยากรณ์", "#ไพ่ยิปซี", "#ดูดวง", "#ดวงการเงิน", "#ความสำเร็จ", "#แรงบันดาลใจ",
            "#พัฒนาตัวเอง", "#เป้าหมายชีวิต", "#โชคลาภ", "#สายมู", "#เปิดไพ่", "#โอกาสใหม่",
            "#ดวงวันนี้", "#กำลังใจ", "#ชีวิตที่ดีขึ้น",
        }) spec.Facebook.Hashtags.Add(h);

        foreach (var t in new[]
        {
            "บุษบาพยากรณ์", "ไพ่ยิปซี", "ดูดวง", "ดวงการเงิน", "ความสำเร็จ", "แรงบันดาลใจ",
            "พัฒนาตัวเอง", "เป้าหมายชีวิต", "โชคลาภ", "สายมู", "เปิดไพ่", "โอกาสใหม่",
            "ดวงวันนี้", "กำลังใจ", "ชีวิตที่ดีขึ้น", "การเงิน", "ความมั่งคั่ง", "ความหวัง",
            "คำทำนาย", "พลังบวก",
        }) spec.Facebook.CommaTags.Add(t);

        spec.Facebook.NotifyDerived();
        return spec;
    }

    private static StoryboardClip Clip(int index, string title, string visual, string camera,
        string sfx, string audio, string[] cards, string[] lines)
    {
        var clip = new StoryboardClip
        {
            Index = index,
            Title = title,
            DurationSec = 8,
            Visual = visual,
            Camera = camera,
            Sfx = sfx,
            AudioBehavior = audio,
        };
        foreach (var l in lines) clip.Dialogue.Add(new DialogueLine(l));
        foreach (var c in cards) clip.Cards.Add(c);
        return clip;
    }
}
