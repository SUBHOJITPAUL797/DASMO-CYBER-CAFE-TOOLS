namespace SmartSaver.Models;

public record GovtPortalPreset(
    string Name,
    string Category,
    int TargetSizeKB,
    int? WidthPx = null,
    int? HeightPx = null,
    string RecommendedFormat = "Same as input",
    string Description = ""
)
{
    public string DisplayName => Name;
    public override string ToString() => Name;
}

public static class GovtPortalPresets
{
    public static readonly List<GovtPortalPreset> All = new()
    {
        new("⚡ Select Govt / Exam Portal Preset...", "Standard", 200, null, null, "Same as input", "Choose a preset to auto-configure"),

        // Photos & Signatures
        new("🌐 Portal / High-Res Doc (1500 × 1000 px — 200 KB)", "Portals", 200, 1500, 1000, ".jpg", "1500 x 1000 px Portal Upload limit"),
        new("🇮🇳 Standard Passport Size (350 × 450 px — 50 KB)", "Photos", 50, 350, 450, ".jpg", "350 x 450 px | Standard Passport"),
        new("🇮🇳 Passport Photo (SSC / State Exams — 50 KB)", "Photos", 50, 413, 531, ".jpg", "3.5 x 4.5 cm | 20 KB - 50 KB JPG"),
        new("🇮🇳 Passport Photo (UPSC / IBPS / Bank — 50 KB)", "Photos", 50, 200, 230, ".jpg", "200 x 230 px | 20 KB - 50 KB JPG"),
        new("✍️ Candidate Signature (Standard — 20 KB)", "Signatures", 20, 140, 60, ".jpg", "140 x 60 px | 10 KB - 20 KB JPG"),
        new("✍️ Candidate Signature (SSC / IBPS — 20 KB)", "Signatures", 20, 280, 120, ".jpg", "3.5 x 1.5 cm | 10 KB - 20 KB JPG"),
        new("📸 Left Thumb Impression (LTI — 50 KB)", "Photos", 50, 240, 240, ".jpg", "Square thumb scan | 10 KB - 50 KB JPG"),

        // Identity & Government Cards
        new("🪪 Aadhaar Card Scan (Under 100 KB PDF)", "Documents", 100, null, null, ".pdf", "Standard Aadhaar Portal upload limit"),
        new("🪪 Aadhaar Card Scan (Under 200 KB PDF)", "Documents", 200, null, null, ".pdf", "Standard 200 KB Aadhaar scan"),
        new("💳 PAN Card / Voter ID (Under 100 KB JPG)", "Documents", 100, null, null, ".jpg", "Front & Back PAN/Voter card"),
        new("💳 PAN Card / Voter ID (Under 200 KB PDF)", "Documents", 200, null, null, ".pdf", "200 KB PDF limit for PAN card correction"),

        // Certificates & Land Records
        new("📜 Income / Caste / Domicile Certificate (200 KB PDF)", "Certificates", 200, null, null, ".pdf", "E-District / Panchayat portal limit (200 KB)"),
        new("📜 Land Record (ROR / Khatian / Deed — 300 KB PDF)", "Certificates", 300, null, null, ".pdf", "Banglarbhumi / Bhulekh portal limit (300 KB)"),
        new("🎓 10th / 12th Marks Card & Certificate (200 KB PDF)", "Certificates", 200, null, null, ".pdf", "Admission / Job application limit (200 KB)"),
        new("🏦 Bank Passbook / Cancelled Cheque (150 KB PDF)", "Financial", 150, null, null, ".pdf", "DBT / Scholarship / Pension limit (150 KB)"),
        
        // Standard File Limits
        new("📄 General Upload Limit — 100 KB", "Standard", 100, null, null, "Same as input", "Strict 100 KB limit"),
        new("📄 General Upload Limit — 200 KB", "Standard", 200, null, null, "Same as input", "Common 200 KB limit"),
        new("📄 General Upload Limit — 500 KB", "Standard", 500, null, null, "Same as input", "500 KB limit")
    };
}
