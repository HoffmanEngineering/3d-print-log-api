#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PrintLogApi.Models;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Deterministic SHA-256 fingerprints of the caller-provided create-tool arguments
    /// (pre-server-defaulting). Uses a BinaryWriter so every string is length-prefixed — a value
    /// cannot forge a field boundary (no delimiter injection). Fixed field order; material rows are
    /// sorted by (materialId, source, estimatedSource) so row order is irrelevant. Null and empty are
    /// distinguished by a leading has-value flag.
    /// <para>
    /// Hashes values EXACTLY as given: it must not normalize anything the persistence path does not,
    /// or it would report two calls as the same request while the database stored different rows.
    /// Callers canonicalize first (see PrintService.CreatePrintForMcp).
    /// </para>
    /// </summary>
    public static class McpRequestFingerprint
    {
        public static string ComputeCreatePrint(
            string? title, long printerId, Print.PrintStatus status,
            DateTimeOffset? startedAt, int? durationSeconds, int? estimatedDurationSeconds,
            string? notes, Guid? projectId, string? fileName, string? url,
            Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
            IReadOnlyList<MaterialUsageInput>? materials)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteStr(w, title);
                w.Write(printerId);
                w.Write((int)status);
                WriteDate(w, startedAt);
                WriteInt(w, durationSeconds);
                WriteInt(w, estimatedDurationSeconds);
                WriteStr(w, notes);
                WriteGuid(w, projectId);
                WriteStr(w, fileName);
                WriteStr(w, url);
                WriteEnum(w, viewStatus.HasValue ? (int?)viewStatus.Value : null);
                WriteBool(w, allowComments);
                WriteBool(w, allowFileDownloads);

                var rows = (materials ?? Array.Empty<MaterialUsageInput>())
                    .OrderBy(m => m.MaterialId)
                    .ThenBy(m => m.Source.HasValue ? (int)m.Source.Value : -1)
                    .ThenBy(m => m.EstimatedSource.HasValue ? (int)m.EstimatedSource.Value : -1)
                    .ToList();
                w.Write(rows.Count);
                foreach (var m in rows)
                {
                    w.Write(m.MaterialId.ToByteArray());
                    WriteEnum(w, m.Source.HasValue ? (int?)m.Source.Value : null);
                    WriteDbl(w, m.Amount);
                    WriteEnum(w, m.EstimatedSource.HasValue ? (int?)m.EstimatedSource.Value : null);
                    WriteDbl(w, m.EstimatedAmount);
                    WriteStr(w, m.Notes);
                }
            }
            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        /// <summary>
        /// Fingerprint of the caller-provided create_material arguments. Same rules as
        /// <see cref="ComputeCreatePrint"/>: fixed field order, length-prefixed strings, has-value
        /// flags, and values hashed EXACTLY as given (the caller canonicalizes first).
        /// <para>
        /// Colors are written in input order because order is meaningful — Colors[0] becomes ColorHex.
        /// Effects are sorted and deduped because they are a set: a reordered list is the same request.
        /// </para>
        /// </summary>
        public static string ComputeCreateMaterial(MaterialAttributesInput input)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteStr(w, input.DisplayName);
                WriteStr(w, input.MaterialType);
                WriteStr(w, input.MaterialCategoryNickname);
                WriteDbl(w, input.DensityGramPerCubicCm);
                WriteDbl(w, input.DiameterMm);
                WriteEnum(w, input.Source.HasValue ? (int?)input.Source.Value : null);
                WriteDbl(w, input.InitialAmount);
                WriteStr(w, input.Brand);
                WriteStr(w, input.ColorName);
                WriteStr(w, input.ColorHex);

                var colors = input.Colors;
                w.Write(colors != null);
                if (colors != null)
                {
                    w.Write(colors.Length);
                    foreach (var color in colors)
                    {
                        WriteStr(w, color);
                    }
                }

                WriteEnum(w, input.ColorPattern.HasValue ? (int?)input.ColorPattern.Value : null);
                WriteEnum(w, input.FinishType.HasValue ? (int?)input.FinishType.Value : null);

                var effects = input.Effects;
                w.Write(effects != null);
                if (effects != null)
                {
                    var ordered = effects.Select(e => (int)e).Distinct().OrderBy(e => e).ToList();
                    w.Write(ordered.Count);
                    foreach (var effect in ordered)
                    {
                        w.Write(effect);
                    }
                }

                WriteStr(w, input.StorageLocation);
                WriteBool(w, input.IsActive);
                WriteBool(w, input.IsFavorite);
                WriteStr(w, input.Notes);
                WriteDbl(w, input.SpoolWeightGrams);
                WriteDbl(w, input.InitialTotalWeightGrams);
                WriteDbl(w, input.TempRangeStartC);
                WriteDbl(w, input.TempRangeEndC);
                WriteDbl(w, input.RecommendedTempC);
                WriteDbl(w, input.RecommendedBedTempC);
                WriteDbl(w, input.InitialLayerTimeS);
                WriteDbl(w, input.LayerTimeS);
                WriteDbl(w, input.MeltingTemperatureC);
                WriteStr(w, input.InertGas);
                WriteDbl(w, input.MaterialRefreshRatio);
                WriteDate(w, input.PurchaseDate);
                WriteStr(w, input.PurchaseLocation);
                WriteStr(w, input.PurchasePriceValue);
                WriteStr(w, input.PurchasePriceCurrency);
                WriteStr(w, input.PurchaseNotes);
            }
            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        /// <summary>
        /// Fingerprint of the caller-provided create_printer arguments. Same rules as
        /// <see cref="ComputeCreatePrint"/>: fixed field order, length-prefixed strings, has-value
        /// flags, and values hashed EXACTLY as given (the caller canonicalizes first).
        /// <para>
        /// Hashed BEFORE server defaulting, so an omitted categoryNickname and an explicit "FFF" are
        /// different requests — which is right: they are different arguments, and a key reused across
        /// them should be reported rather than silently replayed.
        /// </para>
        /// </summary>
        public static string ComputeCreatePrinter(PrinterAttributesInput input)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteStr(w, input.Make);
                WriteStr(w, input.Model);
                WriteStr(w, input.Name);
                WriteStr(w, input.Description);
                WriteStr(w, input.CategoryNickname);
                WriteDbl(w, input.NozzleDiameterMm);
                WriteDbl(w, input.FilamentDiameterMm);
                WriteDbl(w, input.BeamDiameterMm);
                WriteDbl(w, input.BedWidthMm);
                WriteDbl(w, input.BedDepthMm);
                WriteDbl(w, input.BedHeightMm);
                WriteDbl(w, input.ScreenResolutionXPixels);
                WriteDbl(w, input.ScreenResolutionYPixels);
                WriteBool(w, input.HasHeatedBed);
                WriteBool(w, input.HasHeatedChamber);
                WriteDbl(w, input.WattageW);
                WriteBool(w, input.IsActive);
            }
            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        /// <summary>
        /// Fingerprint of the caller-provided create_project arguments. Same rules as
        /// <see cref="ComputeCreatePrint"/>: fixed field order, length-prefixed strings, has-value
        /// flags, and values hashed EXACTLY as given.
        /// <para>
        /// status and viewStatus are non-nullable at the tool boundary (they carry tool-level
        /// defaults), so they are always written.
        /// </para>
        /// </summary>
        public static string ComputeCreateProject(
            string? name, string? reference, string? description, string? url,
            Project.ProjectStatus status, Project.ProjectViewStatus viewStatus)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteStr(w, name);
                WriteStr(w, reference);
                WriteStr(w, description);
                WriteStr(w, url);
                w.Write((int)status);
                w.Write((int)viewStatus);
            }
            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        /// <summary>
        /// Fingerprint of the caller-provided create_feedback arguments. Same rules as
        /// <see cref="ComputeCreatePrint"/>: fixed field order, length-prefixed strings, and values
        /// hashed EXACTLY as given (the caller canonicalizes first).
        /// <para>
        /// Covers only what the caller supplies. The submitter is token-derived and the contact
        /// address is resolved server-side, so neither is an argument and neither belongs here.
        /// </para>
        /// </summary>
        public static string ComputeCreateFeedback(Feedback.FeedbackType type, string? note)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write((int)type);
                WriteStr(w, note);
            }
            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        private static void WriteStr(BinaryWriter w, string? v) { w.Write(v != null); if (v != null) w.Write(v); }
        private static void WriteInt(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteEnum(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteBool(BinaryWriter w, bool? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteDbl(BinaryWriter w, double? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteGuid(BinaryWriter w, Guid? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value.ToByteArray()); }
        private static void WriteDate(BinaryWriter w, DateTimeOffset? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value.ToUniversalTime().Ticks); }
    }
}
