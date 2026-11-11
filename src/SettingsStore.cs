using System.Diagnostics;
using System.Text.Json;

namespace SpendMeMaybe;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = true };
    private long? saveAt;
    private long retryAt;
    public bool Protected { get; private set; }
    public string Notice { get; private set; } = "";

    public void QueueSave(long now)
    {
        if (!Protected) saveAt = now + Stopwatch.Frequency / 2;
    }

    public bool SavePending(Settings settings, long now, bool flush = false)
    {
        if (Protected || saveAt is not { } due || (!flush && (now < due || now < retryAt))) return false;
        if (Save(settings))
        {
            saveAt = null;
            retryAt = 0;
            return true;
        }
        retryAt = now + 2 * Stopwatch.Frequency;
        return false;
    }
    public Settings Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            Span<byte> prefix = stackalloc byte[4];
            var read = stream.Read(prefix); stream.Position = 0;
            // Preserve BOM-detected UTF-16/32 files previously accepted by File.ReadAllText.
            var legacyEncoding = read >= 2 && (prefix[0] == 0xff && prefix[1] == 0xfe || prefix[0] == 0xfe && prefix[1] == 0xff)
                || read >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff;
            Settings? s;
            if (legacyEncoding)
            {
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                s = JsonSerializer.Deserialize<Settings>(reader.ReadToEnd(), Options);
            }
            else s = JsonSerializer.Deserialize<Settings>(stream, Options);
            if (s == null) throw new InvalidDataException("空の設定");
            s.Normalize(); return s;
        }
        catch (Exception ex)
        {
            Protected = true; Notice = "設定を読み込めませんでした。元ファイルを保護し、自動保存を停止しています。無効化後に設定ファイルを確認してください。";
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name); return new();
        }
    }
    public bool Save(Settings settings)
    {
        if (Protected) return false;
        var temp = path + ".tmp";
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings, Options); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            Notice = ""; return true;
        }
        catch { Notice = "設定を保存できませんでした。保存先の空き容量・アクセス権を確認してください。"; return false; }
    }
}
