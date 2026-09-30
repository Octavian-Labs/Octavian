using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OctavianRSM.Services;

// Сверка версий плагинов с codefling.com: slug → страница → «What's New in Version x.y.z»
public class CodeflingService
{
    private const string Base = "https://codefling.com";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "OctavianRSM" } }
    };

    // Кандидаты slug: "GatherManager" → gather-manager, gathermanager
    private static IEnumerable<string> Slugs(string name)
    {
        var n = Regex.Replace(name.Trim(), @"([a-z0-9])([A-Z])", "$1-$2");
        n = Regex.Replace(n, @"[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant();
        yield return n;
        var plain = n.Replace("-", "");
        if (plain != n) yield return plain;
    }

    private static readonly Regex[] VersionRes =
    {
        new(@"What'?s New in Version\s*<[^>]*>\s*([0-9][0-9a-zA-Z.\-_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"What'?s New in Version\s+([0-9][0-9a-zA-Z.\-_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"""version""\s*:\s*""([0-9][0-9a-zA-Z.\-_]*)""", RegexOptions.Compiled),
        new(@"Version\s+([0-9]+\.[0-9][0-9a-zA-Z.\-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private static string? ExtractVersion(string html)
    {
        foreach (var re in VersionRes)
        {
            var m = re.Match(html);
            if (m.Success) return m.Groups[1].Value.TrimEnd('.', '-', '_');
        }
        return null;
    }

    private async Task<(string url, string html)?> FetchPage(string url)
    {
        try
        {
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync();
            return (url, html);
        }
        catch { return null; }
    }

    // Ищет страницу плагина и возвращает (url, версия) либо null
    public async Task<(string Url, string Version)?> FindLatestAsync(string pluginName)
    {
        // 1) Прямые slug-кандидаты
        foreach (var slug in Slugs(pluginName))
        {
            var page = await FetchPage($"{Base}/plugins/{slug}");
            if (page != null)
            {
                var v = ExtractVersion(page.Value.html);
                if (v != null) return (page.Value.url, v);
            }
        }
        // 2) Поиск на сайте
        try
        {
            var q = Uri.EscapeDataString(pluginName);
            var search = await FetchPage($"{Base}/search/?q={q}&type=plugins_file");
            if (search == null) return null;
            var linkRe = new Regex(@"href=""https://codefling\.com/plugins/([a-z0-9\-]+)", RegexOptions.IgnoreCase);
            var wanted = Slugs(pluginName).ToHashSet();
            foreach (Match m in linkRe.Matches(search.Value.html).Cast<Match>().Take(8))
            {
                var slug = m.Groups[1].Value.ToLowerInvariant();
                if (!wanted.Contains(slug)) continue;
                var page = await FetchPage($"{Base}/plugins/{slug}");
                if (page != null)
                {
                    var v = ExtractVersion(page.Value.html);
                    if (v != null) return (page.Value.url, v);
                }
            }
        }
        catch { }
        return null;
    }

    // Сравнение версий, мягкое: 2.0.1 > 2.0 → true
    public static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(Norm(remote), out var rv) && Version.TryParse(Norm(local), out var lv))
            return rv > lv;
        return !remote.Equals(local, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string v) => Regex.Replace(v, @"[^\d.]", "");
}
