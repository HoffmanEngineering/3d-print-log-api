using static PrintLogApi.Achievements.AchievementCategory;
using static PrintLogApi.Achievements.AchievementTrigger;

namespace PrintLogApi.Achievements;

/// <summary>
/// The achievement catalog. Lives in code, not the database: copy, thresholds and keys ship with
/// the API. <see cref="Version"/> must be bumped whenever a key or threshold changes (the
/// catalog-hash test enforces it), which gives every user a catch-up pass on their next
/// evaluation.
/// </summary>
public static class AchievementCatalog
{
    public const int Version = 1;

    /// <summary>Tier names, lowest first. A family with N thresholds uses the first N.</summary>
    public static readonly IReadOnlyList<string> TierNames =
        ["Bronze PLA", "Silver PLA", "Gold Silk", "Rainbow Silk", "Carbon Fiber", "Glow-in-the-Dark"];

    private const AchievementTrigger PrintChanged = PrintAdded | PrintUpdated;
    private const AchievementTrigger DateChanged = PrintAdded | PrintUpdated | TimeZoneChanged;

    public static readonly IReadOnlyList<AchievementDefinition> Definitions =
    [
        // Getting started (one-time, in hint order)
        new("first-printer", GettingStarted, "printer-bedslinger", "Bed Leveled",
            "Add your first printer.", "You added your first printer.",
            [1], PrinterChanged, "printers.count", HintOrder: 1),
        new("first-material", GettingStarted, "spool", "Spooled Up",
            "Add your first spool of material.", "You added your first spool.",
            [1], FilamentChanged, "filaments.count", HintOrder: 2),
        new("first-print", GettingStarted, "layers", "First Layer",
            "Log your first print.", "You logged your first print.",
            [1], PrintAdded, "prints.count", HintOrder: 3),
        new("plugged-in", GettingStarted, "plug", "Plugged In",
            "Log a print straight from your slicer.", "You logged a print straight from your slicer.",
            [1], PrintAdded, "prints.slicerPlugin", HintOrder: 4),
        new("first-photo", GettingStarted, "camera", "Say Cheese",
            "Add a photo to one of your prints.", "You added your first print photo.",
            [1], PrintImageAdded, "prints.withImage", HintOrder: 5),
        new("maker-profile", GettingStarted, "id-badge", "Maker Profile",
            "Add a display name, profile picture and bio.", "Your maker profile is complete.",
            [1], ProfileChanged, "profile.complete", HintOrder: 6),

        // Milestones (tiered)
        new("prints-logged", Milestones, "numeral", "Prolific Printer",
            "Log {n} prints.", "You've logged {n} prints.",
            [10, 25, 50, 100, 250, 500], PrintAdded, "prints.count"),
        new("print-hours", Milestones, "clock", "Machine Hours",
            "Rack up {n} hours of total print time.", "You've racked up {n} hours of print time.",
            [24, 100, 250, 500, 1000, 2500], PrintChanged, "prints.hours"),
        new("material-used", Milestones, "scale", "Plastic Fantastic",
            "Use {n} kg of material across your prints.", "You've printed {n} kg of material.",
            [1, 5, 10, 25, 50, 100], PrintChanged, "prints.materialKg"),
        new("longest-print", Milestones, "timer", "Marathon",
            "Successfully finish a single print that ran {n} hours.", "You finished a {n}-hour print.",
            [8, 24, 48, 72], PrintChanged, "prints.longestHours"),
        new("printers-owned", Milestones, "factory", "Print Farm",
            "Have {n} printers in your workshop.", "You have {n} printers in your workshop.",
            [2, 3, 5, 10], PrinterChanged, "printers.count"),
        new("materials-owned", Milestones, "shelf", "Spool Collector",
            "Add {n} spools of material.", "You've added {n} spools of material.",
            [5, 10, 25, 50, 100], FilamentChanged, "filaments.count"),
        new("material-types", Milestones, "flask", "Material Scientist",
            "Print with {n} different material types.", "You've printed with {n} material types.",
            [2, 3, 5, 8], PrintChanged | FilamentChanged, "prints.materialTypes"),
        new("multi-material", Milestones, "palette", "Technicolor",
            "Log a single print that uses {n} materials.", "You logged a print with {n} materials.",
            [2, 4, 8, 16], PrintChanged, "prints.maxDistinctMaterials"),
        new("projects", Milestones, "folder-star", "Project Manager",
            "Organize prints into {n} project{s}.", "You've organized {n} project{s}.",
            [1, 5, 10, 25], ProjectChanged, "projects.count"),

        // Streaks (tiered, 48-hour rule, user time zone)
        new("daily-streak", Streaks, "flame", "On a Roll",
            "Print {n} days in a row.", "You printed {n} days in a row.",
            [3, 5, 10, 20, 30], DateChanged, "streak.daily"),
        new("weekly-streak", Streaks, "calendar", "Every Week",
            "Print at least once a week for {n} weeks running.", "You printed every week for {n} weeks.",
            [4, 8, 12, 26, 52], DateChanged, "streak.weekly"),
        new("busy-day", Streaks, "stack-plates", "Full Plate",
            "Start {n} prints in a single day.", "You started {n} prints in one day.",
            [3, 5, 10, 20], DateChanged, "day.maxPrints"),

        // Integrations
        new("slicer-cura", Integrations, "initials:Cu", "Cura Connected",
            "Log a print with the Cura plugin.", "You logged a print from Cura.",
            [1], PrintAdded, "slicer.cura"),
        new("slicer-prusaslicer", Integrations, "initials:Pr", "Prusa Connected",
            "Log a print from PrusaSlicer with the Slicer Uploader.", "You logged a print from PrusaSlicer.",
            [1], PrintAdded, "slicer.prusaslicer"),
        new("slicer-orcaslicer", Integrations, "initials:Or", "Orca Connected",
            "Log a print from OrcaSlicer with the Slicer Uploader.", "You logged a print from OrcaSlicer.",
            [1], PrintAdded, "slicer.orcaslicer"),
        new("slicer-bambustudio", Integrations, "initials:Bs", "Bambu Connected",
            "Log a print from Bambu Studio with the Slicer Uploader.", "You logged a print from Bambu Studio.",
            [1], PrintAdded, "slicer.bambustudio"),
        new("slicer-anycubic", Integrations, "initials:Ac", "Anycubic Connected",
            "Log a print from Anycubic Slicer Next with the Slicer Uploader.", "You logged a print from Anycubic Slicer Next.",
            [1], PrintAdded, "slicer.anycubic"),
        new("slicer-sampler", Integrations, "fork", "Slicer Sampler",
            "Log prints from {n} different slicers.", "You've logged prints from {n} slicers.",
            [2, 3, 4], PrintAdded, "slicer.distinct"),
        new("octoprint", Integrations, "octopus", "Octo Pilot",
            "Have OctoPrint log a print for you automatically.", "OctoPrint logged a print for you.",
            [1], PrintAdded, "source.octoprint"),
        new("moonraker", Integrations, "moon-link", "Klipper Linked",
            "Have Klipper (Moonraker) log a print for you automatically.", "Klipper logged a print for you.",
            [1], PrintAdded, "source.moonraker"),
        new("mcp", Integrations, "robot", "Robot Co-Pilot",
            "Log a print by asking your AI assistant.", "Your AI assistant logged a print for you.",
            [1], PrintAdded, "source.mcp"),

        // Community and care (tiered)
        new("public-prints", Community, "globe", "Show and Tell",
            "Share {n} print{s} publicly.", "You've shared {n} public print{s}.",
            [1, 10, 25, 50], PrintChanged, "prints.public"),
        new("comments-received", Community, "chat", "Conversation Starter",
            "Get comments from {n} different maker{s} on your prints.", "{n} maker{s} commented on your prints.",
            [1, 5, 25, 50], CommentReceived, "comments.distinctCommenters"),
        new("maintenance", Community, "wrench", "Well Oiled",
            "Log {n} printer maintenance task{s}.", "You've logged {n} maintenance task{s}.",
            [1, 5, 10, 25], MaintenanceChanged, "maintenance.count"),

        // Hidden (one-time, revealed when earned)
        new("spaghetti", Hidden, "noodles", "Spaghetti Monster",
            "Log a failed print. Every failure you record is data.", "You logged a failed print. Every failure is data.",
            [1], PrintChanged, "prints.failed"),
        new("comeback", Hidden, "wave", "Welcome Back",
            "Log a print after 30 or more days away.", "Welcome back! Good to see you printing again.",
            [1], DateChanged, "day.comeback"),
        new("night-owl", Hidden, "moon", "Night Owl",
            "Start a print between midnight and 4 AM.", "You started a print in the middle of the night.",
            [1], DateChanged, "day.nightOwl"),
        new("new-year", Hidden, "confetti", "First Print of the Year",
            "Start a print on January 1st.", "You started the year with a print.",
            [1], DateChanged, "day.newYear"),
    ];

    private static readonly Dictionary<string, AchievementDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>Every tier of every family, retired ones excluded.</summary>
    public static readonly int TotalTierCount = Definitions.Where(d => !d.Retired).Sum(d => d.Thresholds.Count);

    public static AchievementDefinition? Find(string key) => ByKey.GetValueOrDefault(key);
}
