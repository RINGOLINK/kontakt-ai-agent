using System.Text.Json;
using Microsoft.Win32;

namespace KontaktLibManager.Core;

/// <summary>一次入库快照的概要信息。</summary>
public sealed class SnapshotInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Reason { get; set; } = "";
    public int ProductCount { get; set; }
    public bool HasPortable { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>
/// 入库状态快照与回滚。
///
/// 覆盖范围（与「五步入库」一致）：
///   · 注册表三视图：HKLM\SOFTWARE\Native Instruments（64 位）、
///     HKLM\SOFTWARE\WOW6432Node\Native Instruments（32 位）、HKCU\SOFTWARE\Native Instruments
///   · 便携版配置文件：UserData\Settings.cfg 与 UserData\Service Center\LibraryHints.xml
///
/// 安全约定：
///   · 快照只**读取**现有状态，不改动任何东西；
///   · 回滚前**先自动快照当前状态**，避免回滚本身成为不可逆操作；
///   · 回滚只写快照里记录过的键与值，不删除快照之外的任何内容。
/// </summary>
public static class RegistrationSnapshot
{
    private const string NiPath = @"SOFTWARE\Native Instruments";

    public static string Dir => Path.Combine(AppPaths.DataDir, "snapshots");

    private sealed class SnapshotFile
    {
        public string CreatedAt { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Machine { get; set; } = "";
        public string KontaktExe { get; set; } = "";
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Registry { get; set; } = new();
        public string? SettingsCfgBase64 { get; set; }
        public string? LibraryHintsBase64 { get; set; }
    }

    /// <summary>创建快照。reason 用于在列表里说明这份快照的用途。</summary>
    public static (bool ok, string message, string path) Capture(string reason = "手动快照")
    {
        try
        {
            var snap = new SnapshotFile
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Reason = reason,
                Machine = Environment.MachineName,
            };

            // ── 注册表三视图 ──
            foreach (var (viewName, hive, view) in new[]
            {
                ("HKLM64", RegistryHive.LocalMachine, RegistryView.Registry64),
                ("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32),
                ("HKCU", RegistryHive.CurrentUser, RegistryView.Default),
            })
            {
                var products = new Dictionary<string, Dictionary<string, string>>();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var ni = baseKey.OpenSubKey(NiPath);
                    if (ni != null)
                    {
                        foreach (var sub in ni.GetSubKeyNames())
                        {
                            // 只记录「产品」键（含 ContentDir），跳过 Content 等索引键
                            using var pk = ni.OpenSubKey(sub);
                            if (pk == null) continue;
                            bool isProduct = pk.GetValueNames().Any(v =>
                                v.Equals("ContentDir", StringComparison.OrdinalIgnoreCase) ||
                                v.Equals("Visibility", StringComparison.OrdinalIgnoreCase));
                            if (!isProduct) continue;

                            var vals = new Dictionary<string, string>();
                            foreach (var vn in pk.GetValueNames())
                            {
                                var v = pk.GetValue(vn);
                                if (v == null) continue;
                                vals[vn] = $"{pk.GetValueKind(vn)}|{v}";
                            }
                            if (vals.Count > 0) products[sub] = vals;
                        }
                    }
                }
                catch { }
                snap.Registry[viewName] = products;
            }

            // ── 便携版配置 ──
            string kontaktExe = KontaktInfo.DetectInstalls()
                .FirstOrDefault(i => VersionUtil.IsValid(i.Version))?.Path ?? "";
            snap.KontaktExe = kontaktExe;
            if (kontaktExe.Length > 0)
            {
                var info = PortableKontaktStore.Inspect(kontaktExe);
                if (info != null && info.Exists)
                {
                    try { snap.SettingsCfgBase64 = Convert.ToBase64String(File.ReadAllBytes(info.SettingsCfg)); } catch { }
                    try
                    {
                        string hints = Path.Combine(Path.GetDirectoryName(info.SettingsCfg)!, "Service Center", "LibraryHints.xml");
                        if (File.Exists(hints)) snap.LibraryHintsBase64 = Convert.ToBase64String(File.ReadAllBytes(hints));
                    }
                    catch { }
                }
            }

            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }),
                              new System.Text.UTF8Encoding(false));

            int count = snap.Registry.Values.Sum(v => v.Count);
            return (true, $"已创建快照：{count} 个产品 / {snap.Registry.Count} 个注册表视图" +
                          (snap.SettingsCfgBase64 != null ? " + 便携版配置" : ""), file);
        }
        catch (Exception ex)
        {
            return (false, $"创建快照失败：{ex.Message}", "");
        }
    }

    public static List<SnapshotInfo> List()
    {
        var list = new List<SnapshotInfo>();
        try
        {
            var dir = new DirectoryInfo(Dir);
            if (!dir.Exists) return list;
            foreach (var f in dir.EnumerateFiles("snapshot-*.json").OrderByDescending(f => f.Name))
            {
                var info = new SnapshotInfo
                {
                    Path = f.FullName,
                    Name = Path.GetFileNameWithoutExtension(f.Name),
                    SizeBytes = f.Length,
                    CreatedAt = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                try
                {
                    var snap = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(f.FullName));
                    if (snap != null)
                    {
                        info.CreatedAt = snap.CreatedAt;
                        info.Reason = snap.Reason;
                        info.ProductCount = snap.Registry.Values.Sum(v => v.Count);
                        info.HasPortable = snap.SettingsCfgBase64 != null;
                    }
                }
                catch { }
                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    /// <summary>回滚到指定快照（回滚前会自动为当前状态再存一份快照）。</summary>
    public static (bool ok, string message, List<string> details) Restore(string snapshotPath)
    {
        var details = new List<string>();
        try
        {
            if (!File.Exists(snapshotPath)) return (false, "快照文件不存在", details);
            var snap = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(snapshotPath));
            if (snap == null) return (false, "快照文件解析失败", details);

            if (!KontaktInfo.IsAdmin())
                return (false, "回滚注册表需要管理员权限", details);
            if (KontaktInfo.IsRunning())
                return (false, "Kontakt 正在运行，请先完全关闭（它退出时会覆写便携版配置）", details);

            // ① 先把当前状态存一份，回滚本身也可撤销
            var (saved, saveMsg, savePath) = Capture("回滚前自动备份");
            details.Add(saved ? $"回滚前已自动备份：{Path.GetFileName(savePath)}" : $"自动备份失败：{saveMsg}");

            // ② 写回注册表
            int written = 0, failed = 0;
            foreach (var (viewName, hive, view) in new[]
            {
                ("HKLM64", RegistryHive.LocalMachine, RegistryView.Registry64),
                ("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32),
                ("HKCU", RegistryHive.CurrentUser, RegistryView.Default),
            })
            {
                if (!snap.Registry.TryGetValue(viewName, out var products)) continue;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var (product, values) in products)
                    {
                        try
                        {
                            using var pk = baseKey.CreateSubKey(Path.Combine(NiPath, product));
                            if (pk == null) { failed++; continue; }
                            foreach (var (vn, encoded) in values)
                            {
                                int bar = encoded.IndexOf('|');
                                if (bar <= 0) continue;
                                string kind = encoded[..bar];
                                string val = encoded[(bar + 1)..];
                                if (kind == "DWord" && int.TryParse(val, out int dw))
                                    pk.SetValue(vn, dw, RegistryValueKind.DWord);
                                else if (kind == "QWord" && long.TryParse(val, out long qw))
                                    pk.SetValue(vn, qw, RegistryValueKind.QWord);
                                else if (kind == "Binary" && val.Length > 0)
                                {
                                    try { pk.SetValue(vn, Convert.FromBase64String(val), RegistryValueKind.Binary); } catch { }
                                }
                                else
                                    pk.SetValue(vn, val, RegistryValueKind.String);
                            }
                            written++;
                        }
                        catch { failed++; }
                    }
                }
                catch (Exception ex) { details.Add($"{viewName} 写入失败：{ex.Message}"); }
            }
            details.Add($"注册表已还原：{written} 个产品" + (failed > 0 ? $"，{failed} 个失败" : ""));

            // ③ 还原便携版配置
            if (snap.SettingsCfgBase64 != null && snap.KontaktExe.Length > 0)
            {
                var info = PortableKontaktStore.Inspect(snap.KontaktExe);
                if (info != null && info.Exists)
                {
                    try
                    {
                        string backup = info.SettingsCfg + $".klm-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                        File.Copy(info.SettingsCfg, backup, overwrite: true);
                        File.WriteAllBytes(info.SettingsCfg, Convert.FromBase64String(snap.SettingsCfgBase64));
                        details.Add($"便携版 Settings.cfg 已还原（还原前备份：{Path.GetFileName(backup)}）");

                        if (snap.LibraryHintsBase64 != null)
                        {
                            string hints = Path.Combine(Path.GetDirectoryName(info.SettingsCfg)!, "Service Center", "LibraryHints.xml");
                            Directory.CreateDirectory(Path.GetDirectoryName(hints)!);
                            File.WriteAllBytes(hints, Convert.FromBase64String(snap.LibraryHintsBase64));
                            details.Add("便携版 LibraryHints.xml 已还原");
                        }
                    }
                    catch (Exception ex) { details.Add($"便携版配置还原失败：{ex.Message}"); }
                }
                else details.Add("未找到便携版 Kontakt，已跳过便携版配置还原");
            }

            return (written > 0 || snap.SettingsCfgBase64 != null, "回滚完成", details);
        }
        catch (Exception ex)
        {
            return (false, $"回滚失败：{ex.Message}", details);
        }
    }

    public static (bool ok, string message) Delete(string snapshotPath)
    {
        try
        {
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            return (true, "已删除该快照");
        }
        catch (Exception ex) { return (false, $"删除失败：{ex.Message}"); }
    }
}
