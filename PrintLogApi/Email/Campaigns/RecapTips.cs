using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Campaigns;

/// <summary>The recap's one tip: the first feature, in this order, the user has never used.</summary>
public static class RecapTips
{
    public static async Task<RecapTip?> ForAsync(PrintLogContext db, long userId, Func<string, string> link, CancellationToken ct)
    {
        if (!await db.Filaments.AnyAsync(f => f.CreatedById == userId, ct))
        {
            return new RecapTip("Track your spools in Materials and see how much filament is left on each.", link("/materials"), "Open Materials");
        }

        if (!await db.DeviceTokens.AnyAsync(d => d.UserId == userId, ct))
        {
            return new RecapTip("The Android app logs prints from your phone and notifies you when they finish.", link("/docs/android-app"), "Get the app");
        }

        if (!await db.PrinterImages.AnyAsync(i => i.Printer.UserId == userId, ct))
        {
            return new RecapTip("Add a photo to each printer so you can tell them apart at a glance.", link("/printers"), "Add a printer photo");
        }

        if (!await db.Prints.AnyAsync(p => p.CreatedById == userId && p.Source == PrintSource.Mcp, ct))
        {
            return new RecapTip("Ask an AI assistant about your prints by connecting it to 3D Print Log.", link("/docs/mcp"), "Set up the MCP server");
        }

        return null;
    }
}
