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
    /// Deterministic SHA-256 fingerprint of the caller-provided create_print arguments
    /// (pre-server-defaulting). Uses a BinaryWriter so every string is length-prefixed — a value
    /// cannot forge a field boundary (no delimiter injection). Fixed field order; material rows are
    /// sorted by (materialId, source, estimatedSource) so row order is irrelevant. Strings trimmed;
    /// null and empty are distinguished by a leading has-value flag.
    /// </summary>
    public static class McpRequestFingerprint
    {
        public static string ComputeCreatePrint(
            string title, long printerId, Print.PrintStatus status,
            DateTimeOffset? startedAt, int? durationSeconds, int? estimatedDurationSeconds,
            string notes, Guid? projectId, string fileName, string url,
            Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
            IReadOnlyList<MaterialUsageInput> materials)
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

        private static void WriteStr(BinaryWriter w, string v) { w.Write(v != null); if (v != null) w.Write(v.Trim()); }
        private static void WriteInt(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteEnum(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteBool(BinaryWriter w, bool? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteDbl(BinaryWriter w, double? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteGuid(BinaryWriter w, Guid? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value.ToByteArray()); }
        private static void WriteDate(BinaryWriter w, DateTimeOffset? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value.ToUniversalTime().Ticks); }
    }
}
