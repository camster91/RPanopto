using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// Loads and saves <see cref="BookingTemplate"/>s, in the same per-user folder as
/// the credentials, the logs and the audit trail.
///
/// <para>Beside those rather than in the repository, for the same reason they are:
/// this is one person's working set on one machine, and anything inside the repo
/// is something to remember not to commit.</para>
///
/// <para>Readable JSON, indented and with the weekdays written as names, because
/// the folder it sits in is one people are invited to edit by hand — the same
/// invitation <c>credentials.json</c> carries. Nothing here is secret: a template
/// is a set of room names and a time.</para>
/// </summary>
public static class BookingTemplateStore
{
    /// <summary><c>~/.panopto-scheduler/templates.json</c></summary>
    public static string DefaultPath => Path.Combine(
        Configuration.CredentialStore.Directory, "templates.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Matches the credentials file: camelCase keys, indented, readable.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The saved templates, or an empty list.
    ///
    /// <para><b>A missing file is not an error here</b>, which is where this
    /// differs from <see cref="Configuration.CredentialStore"/>: credentials that
    /// are absent mean the app cannot start, and templates that are absent mean
    /// nobody has saved one yet, which is the ordinary state of a new install. A
    /// file that is present and unreadable is a different matter and does throw,
    /// so the operator is told rather than shown an empty list that looks like the
    /// templates were deleted.</para>
    /// </summary>
    /// <param name="path">
    /// Defaults to <see cref="DefaultPath"/>; tests pass a temporary one rather
    /// than reading the machine they run on.
    /// </param>
    public static IReadOnlyList<BookingTemplate> Load(string? path = null)
    {
        var file = path ?? DefaultPath;

        if (!File.Exists(file)) return [];

        var templates = JsonSerializer.Deserialize<List<BookingTemplate>>(
            File.ReadAllText(file), ReadOptions);

        if (templates is null) return [];

        // Validated rather than trusted. This file is hand-editable, so a span
        // nobody meant has to be caught here — where the message can name the
        // template and the number — rather than later, as an overflow in the
        // middle of generating rows.
        foreach (var template in templates) template.Validate();

        return templates;
    }

    public static void Save(IReadOnlyList<BookingTemplate> templates, string? path = null)
    {
        foreach (var template in templates) template.Validate();

        var file = path ?? DefaultPath;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // Written beside and moved over, so a crash part-way through leaves the
        // previous file intact rather than a half-written one.
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(templates, WriteOptions));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Adds <paramref name="template"/>, replacing any template with the same name
    /// and keeping the rest.
    ///
    /// <para>Names are compared without case, because the name is what the picker
    /// shows and two entries reading the same are two entries nobody can tell
    /// apart — so saving under a name that is already there is how a template is
    /// updated.</para>
    /// </summary>
    public static IReadOnlyList<BookingTemplate> Upsert(BookingTemplate template, string? path = null)
    {
        template.Validate();

        var templates = Load(path).Where(t => !SameName(t.Name, template.Name)).ToList();
        templates.Add(template);

        Save(templates, path);
        return templates;
    }

    /// <summary>Drops the template with this name, if there is one, and keeps the rest.</summary>
    public static IReadOnlyList<BookingTemplate> Remove(string name, string? path = null)
    {
        var templates = Load(path).Where(t => !SameName(t.Name, name)).ToList();

        Save(templates, path);
        return templates;
    }

    private static bool SameName(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
