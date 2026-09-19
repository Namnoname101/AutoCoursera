using System.Text.Json;
using System.IO;

namespace Acose.CustomerManager;

/// <summary>Stores local edits independently of the metadata received from existing account profiles.</summary>
public sealed class ManagerStore
{
    private const string DataFileName = "manager-data.json";
    private const string MissingName = "CHƯA CÓ HỌ TÊN";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly object _gate = new();
    private bool _preserveBackup;
    private bool _loadFailed;

    public ManagerStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Thư mục dữ liệu không được để trống.", nameof(dataDirectory));
        DataDirectory = CanonicalPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string ProfileDirectory => Path.Combine(DataDirectory, "Profiles");
    public string? LastWarning { get; private set; }
    private string DataPath => Path.Combine(DataDirectory, DataFileName);
    private string BackupPath => DataPath + ".bak";

    public ManagerData Load()
    {
        lock (_gate)
        {
            LastWarning = null;
            _loadFailed = false;
            _preserveBackup = false;
            try
            {
                EnsureNoReparsePoints(DataDirectory);
                bool hasMain = File.Exists(DataPath);
                bool hasBackup = File.Exists(BackupPath);
                if (!hasMain && !hasBackup) return new ManagerData();

                if (hasMain)
                {
                    try { return ReadData(DataPath); }
                    catch (Exception ex) when (IsDataReadError(ex)) { }
                }

                if (hasBackup)
                {
                    try
                    {
                        ManagerData recovered = ReadData(BackupPath);
                        _preserveBackup = true;
                        LastWarning = "Dữ liệu chính bị thiếu hoặc không đọc được. Đã phục hồi từ bản dự phòng gần nhất; hãy kiểm tra các thay đổi cuối.";
                        return recovered;
                    }
                    catch (Exception ex) when (IsDataReadError(ex)) { }
                }

                throw new InvalidDataException("Không đọc được dữ liệu quản lý và bản dự phòng. Dữ liệu hiện có được giữ nguyên; ứng dụng không tạo dữ liệu trống để ghi đè.");
            }
            catch
            {
                _loadFailed = true;
                throw;
            }
        }
    }

    public void Save(ManagerData data)
    {
        lock (_gate)
        {
            if (_loadFailed)
                throw new InvalidOperationException("Chưa thể lưu vì lần đọc dữ liệu trước bị lỗi. Cần khôi phục dữ liệu rồi mở lại ứng dụng.");
            ValidateData(data);
            EnsureNoReparsePoints(DataDirectory);
            Directory.CreateDirectory(DataDirectory);
            EnsureNoReparsePoints(DataDirectory);
            EnsureNoReparsePoints(DataPath);
            EnsureNoReparsePoints(BackupPath);

            string temporaryPath = Path.Combine(DataDirectory, $".{DataFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, data, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(DataPath))
                {
                    // After recovery, keep the readable backup instead of replacing it with the damaged main file.
                    File.Replace(temporaryPath, DataPath, _preserveBackup ? null : BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, DataPath);
                }
                _preserveBackup = false;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Returns the number of customers added. Local names and course edits remain authoritative.</summary>
    public int ImportExisting(ManagerData data)
    {
        lock (_gate)
        {
            ValidateData(data);
            LastWarning = null;
            int added = 0;
            int skipped = 0;
            int invalidLinks = 0;
            var knownProfiles = new Dictionary<string, CustomerOrder>(PathComparer);
            foreach (CustomerOrder order in data.Orders)
                knownProfiles.TryAdd(CanonicalPath(order.ProfilePath), order);

            foreach (string root in data.ImportRoots.Distinct(PathComparer))
            {
                string rootPath;
                try
                {
                    rootPath = CanonicalPath(root);
                    EnsureNoReparsePoints(rootPath);
                    if (!Directory.Exists(rootPath)) continue;
                }
                catch (Exception ex) when (IsDataReadError(ex)) { skipped++; continue; }

                foreach (string group in new[] { "_ManagedAccounts", "_CourseJobs" })
                {
                    string groupPath = Path.Combine(rootPath, group);
                    string[] profiles;
                    try
                    {
                        EnsureWithin(rootPath, groupPath);
                        EnsureNoReparsePoints(groupPath);
                        if (!Directory.Exists(groupPath)) continue;
                        // Only direct profile folders are eligible; browser cache and cookie stores are never read.
                        profiles = Directory.GetDirectories(groupPath, "*", SearchOption.TopDirectoryOnly);
                    }
                    catch (Exception ex) when (IsDataReadError(ex)) { skipped++; continue; }

                    foreach (string candidate in profiles.OrderBy(x => x, PathComparer))
                    {
                        try
                        {
                            string profile = CanonicalPath(candidate);
                            EnsureWithin(groupPath, profile);
                            EnsureNoReparsePoints(profile);
                            AccountMetadata metadata = ReadAccountMetadata(profile);
                            List<string> urls = ReadCourseUrls(metadata.LaunchUrl, ref invalidLinks);
                            if (!knownProfiles.TryGetValue(profile, out CustomerOrder? order))
                            {
                                order = new CustomerOrder
                                {
                                    Name = ImportedName(metadata),
                                    ProfilePath = profile,
                                    Source = "Đã có trên máy"
                                };
                                data.Orders.Add(order);
                                knownProfiles.Add(profile, order);
                                added++;
                            }

                            var observed = new HashSet<string>(order.ObservedExternalUrls, StringComparer.OrdinalIgnoreCase);
                            var current = new HashSet<string>(order.Courses.Select(x => x.Url), StringComparer.OrdinalIgnoreCase);
                            foreach (string url in urls)
                            {
                                if (!observed.Add(url)) continue;
                                order.ObservedExternalUrls.Add(url);
                                if (current.Add(url)) order.Courses.Add(new CourseItem { Url = url });
                            }
                            order.Refresh();
                        }
                        catch (Exception ex) when (IsDataReadError(ex)) { skipped++; }
                    }
                }
            }

            var warnings = new List<string>();
            if (skipped > 0)
                warnings.Add($"Đã bỏ qua {skipped} thư mục hoặc hồ sơ không đọc được hay là đường dẫn liên kết. Dữ liệu đã nhập vẫn được giữ nguyên.");
            if (invalidLinks > 0)
                warnings.Add($"Đã bỏ qua {invalidLinks} link không hợp lệ trong dữ liệu nguồn. Bạn có thể bổ sung link khóa học bằng tay.");
            LastWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
            return added;
        }
    }

    public string AllocateProfilePath(string orderId)
    {
        if (string.IsNullOrEmpty(orderId) || orderId.Length > 100 ||
            orderId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Mã đơn không hợp lệ để tạo hồ sơ đăng nhập.", nameof(orderId));

        lock (_gate)
        {
            string profile = CanonicalPath(Path.Combine(ProfileDirectory, orderId));
            EnsureWithin(DataDirectory, ProfileDirectory);
            EnsureWithin(ProfileDirectory, profile);
            EnsureNoReparsePoints(profile);
            Directory.CreateDirectory(profile);
            EnsureNoReparsePoints(profile);
            return profile;
        }
    }

    private static ManagerData ReadData(string path)
    {
        EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Dữ liệu quản lý không hợp lệ.");
        JsonElement version = default;
        int versionFields = 0;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals(nameof(ManagerData.Version), StringComparison.OrdinalIgnoreCase)) continue;
            version = property.Value;
            versionFields++;
        }
        if (versionFields != 1 || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value != 1)
            throw new InvalidDataException("Phiên bản dữ liệu chưa được hỗ trợ; cần giữ nguyên tệp hiện có.");
        ManagerData data = document.RootElement.Deserialize<ManagerData>(JsonOptions)
            ?? throw new InvalidDataException("Dữ liệu quản lý trống.");
        ValidateData(data);
        return data;
    }

    private static void ValidateData(ManagerData data)
    {
        if (data is null || data.Version != 1 || data.Orders is null || data.Runs is null || data.ImportRoots is null)
            throw new InvalidDataException("Cấu trúc dữ liệu quản lý không hợp lệ.");
        if (data.MaxParallel is < 1 or > 64)
            throw new InvalidDataException("Số khóa chạy đồng thời phải từ 1 đến 64.");
        var orderIds = new HashSet<string>(StringComparer.Ordinal);
        var courseIds = new HashSet<string>(StringComparer.Ordinal);
        var profilePaths = new HashSet<string>(PathComparer);
        foreach (CustomerOrder order in data.Orders)
        {
            if (order is null || string.IsNullOrWhiteSpace(order.Id) || !orderIds.Add(order.Id) ||
                string.IsNullOrWhiteSpace(order.Name) || string.IsNullOrWhiteSpace(order.ProfilePath) ||
                order.Courses is null || order.ObservedExternalUrls is null || order.Source is null)
                throw new InvalidDataException("Thông tin đơn khách hàng không hợp lệ.");
            if (!Path.IsPathFullyQualified(order.ProfilePath) || !profilePaths.Add(CanonicalPath(order.ProfilePath)))
                throw new InvalidDataException("Hồ sơ đăng nhập bị trùng hoặc có đường dẫn không hợp lệ.");
            foreach (CourseItem course in order.Courses)
            {
                if (course is null || string.IsNullOrWhiteSpace(course.Id) || !courseIds.Add(course.Id) ||
                    string.IsNullOrWhiteSpace(course.Url) || !Enum.IsDefined(course.State) || course.Activity is null)
                    throw new InvalidDataException("Thông tin khóa học không hợp lệ.");
                InputRules.CourseUrl(course.Url);
            }
            foreach (string url in order.ObservedExternalUrls)
            {
                if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Danh sách link đã nhập không hợp lệ.");
                InputRules.CourseUrl(url);
            }
        }
        if (data.ImportRoots.Any(string.IsNullOrWhiteSpace) || data.ImportRoots.Any(x => !Path.IsPathFullyQualified(x)))
            throw new InvalidDataException("Thư mục nhận tài khoản phải là đường dẫn đầy đủ.");
        if (data.Runs.Any(x => x is null))
            throw new InvalidDataException("Thông tin phiên chạy không hợp lệ.");
    }

    private sealed record AccountMetadata(string? DisplayName, string? FullName, string? LaunchUrl);

    private static AccountMetadata ReadAccountMetadata(string profile)
    {
        string? displayName = null;
        string? fullName = null;
        string? launchUrl = null;
        using (JsonDocument? manager = ReadMetadata(Path.Combine(profile, ".acose-account-manager.json")))
        {
            if (manager is not null)
            {
                displayName = OptionalString(manager.RootElement, "displayName");
                launchUrl = OptionalString(manager.RootElement, "launchUrl");
            }
        }
        using (JsonDocument? identity = ReadMetadata(Path.Combine(profile, ".acose-profile-identity.json")))
        {
            if (identity is not null) fullName = OptionalString(identity.RootElement, "fullName");
        }
        return new AccountMetadata(displayName, fullName, launchUrl);
    }

    private static JsonDocument? ReadMetadata(string path)
    {
        EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Tệp thông tin hồ sơ vượt quá kích thước cho phép.");
        JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Thông tin hồ sơ không hợp lệ.");
        }
        return document;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Thông tin hồ sơ không hợp lệ.");
        return value.GetString();
    }

    private static string ImportedName(AccountMetadata metadata)
    {
        // displayName is an explicit local label in the previous manager, so it takes precedence over login identity.
        foreach (string? candidate in new[] { metadata.DisplayName, metadata.FullName })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return InputRules.FullName(candidate); }
            catch (InvalidOperationException) { }
        }
        return MissingName;
    }

    private static List<string> ReadCourseUrls(string? launchUrl, ref int invalidLinks)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (launchUrl ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                string url = InputRules.CourseUrl(line);
                if (new Uri(url).AbsolutePath.Trim('/').Length == 0) continue;
                urls.Add(url);
            }
            catch (InvalidOperationException) { invalidLinks++; }
        }
        return urls.ToList();
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void EnsureWithin(string parent, string child)
    {
        string relative = Path.GetRelativePath(CanonicalPath(parent), CanonicalPath(child));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Đường dẫn nằm ngoài thư mục hồ sơ cho phép.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        string? current = CanonicalPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Không sử dụng thư mục hoặc tệp liên kết để lưu hay nhập hồ sơ.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsDataReadError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            NotSupportedException or InvalidOperationException;
}
