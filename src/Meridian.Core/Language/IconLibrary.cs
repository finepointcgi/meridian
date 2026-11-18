using System.Globalization;
using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>One icon of the cloud and network libraries: a free shape whose subtype is <see cref="Subtype"/>.</summary>
/// <param name="Key">Short id; the shape's subtype is <c>icon.</c> + key.</param>
/// <param name="Name">Generic display name (never a vendor product name).</param>
/// <param name="Section">Shapes panel section: "Cloud" or "Network".</param>
/// <param name="Path">Line drawing as SVG path data on a 24 × 24 grid, stroked in one colour, never filled.</param>
/// <param name="Container">Drawn as a group frame with the icon in its corner; shapes created inside it nest.</param>
/// <param name="Synonyms">Extra words the shapes panel search matches.</param>
public sealed record IconDef(string Key, string Name, string Section, string Path, bool Container, IReadOnlyList<string> Synonyms)
{
    public string Subtype => IconLibrary.Prefix + Key;
}

/// <summary>
/// Cloud and network icons for whiteboards (and the optional icon of a deployment node). The drawings are original,
/// simple line icons in one colour, so they follow the theme and the shape's line colour. No vendor artwork or logos:
/// users who want to name a product type it into the free-text <see cref="ProviderField"/> ("AWS Lambda").
/// </summary>
public static class IconLibrary
{
    public const string Prefix = "icon.";
    public const string CloudSection = "Cloud";
    public const string NetworkSection = "Network";

    /// <summary>Free text shown under an icon's name, e.g. the product it stands for ("Azure SQL", "S3").</summary>
    public const string ProviderField = "provider";

    /// <summary>Deployment nodes, devices and execution environments: key of the icon shown in the corner.</summary>
    public const string IconField = "icon";

    public const double GridSize = 24;

    private static string C(double cx, double cy, double r) => string.Create(CultureInfo.InvariantCulture,
        $"M{cx - r},{cy}a{r},{r} 0 1 0 {2 * r},0a{r},{r} 0 1 0 {-2 * r},0");

    public static readonly IReadOnlyList<IconDef> All =
    [
        // ---- Cloud
        new("cloud", "Cloud", CloudSection,
            "M7.5 18.5h10a4 4 0 0 0 .6-7.95A5.5 5.5 0 0 0 7.6 9.3A4.6 4.6 0 0 0 7.5 18.5z", false,
            ["public cloud", "private cloud", "saas", "paas", "iaas"]),
        new("region", "Region", CloudSection,
            "M12 21s-6-5.6-6-10.5a6 6 0 0 1 12 0C18 15.4 12 21 12 21z" + C(12, 10.5, 2.2), true,
            ["location", "geography", "data centre", "data center", "datacenter"]),
        new("zone", "Availability zone", CloudSection,
            "M3 3h11v11H3z M10 10h11v11H10z", true,
            ["az", "zone", "fault domain"]),
        new("vnet", "Virtual network", CloudSection,
            "M3 4h18v16H3z" + C(8, 9, 1.8) + C(16, 9, 1.8) + C(12, 15.5, 1.8) + "M9.8 9h4.4 M9 10.6l2.1 3.2 M15 10.6l-2.1 3.2", true,
            ["vpc", "vnet", "virtual private cloud", "network", "private network"]),
        new("subnet", "Subnet", CloudSection,
            "M3 4h18v16H3z M7 8h10v8H7z M7 12h10", true,
            ["sub-network", "segment", "cidr", "private subnet", "public subnet"]),
        new("cluster", "Container cluster", CloudSection,
            "M9 3h6v6H9z M3 15h6v6H3z M15 15h6v6h-6z M12 9v3 M6 15v-3h12v3", true,
            ["kubernetes", "k8s", "eks", "aks", "gke", "openshift", "swarm", "ecs", "orchestration"]),
        new("server", "Server / VM", CloudSection,
            "M4 4h16v6H4z M4 14h16v6H4z M11 7h6 M11 17h6" + C(7.5, 7, 0.9) + C(7.5, 17, 0.9), false,
            ["vm", "virtual machine", "ec2", "compute", "instance", "host", "server", "compute engine"]),
        new("container", "Container", CloudSection,
            "M12 3l8 4.5v9L12 21l-8-4.5v-9z M4 7.5l8 4.5 8-4.5 M12 12v9", false,
            ["docker", "pod", "image", "fargate", "container app"]),
        new("function", "Serverless function", CloudSection,
            "M13.5 2.5 5 13.5h6l-1 8 8.5-11h-6z", false,
            ["lambda", "function", "faas", "cloud function", "azure function", "serverless"]),
        new("apigw", "API gateway", CloudSection,
            "M4 21V8l8-5 8 5v13 M10 11l-2.5 3 2.5 3 M14 11l2.5 3-2.5 3", false,
            ["api", "gateway", "api management", "rest", "endpoint", "apim"]),
        new("database", "Database", CloudSection,
            "M5 6a7 2.5 0 1 0 14 0a7 2.5 0 1 0-14 0v12a7 2.5 0 0 0 14 0V6 M5 12a7 2.5 0 0 0 14 0", false,
            ["db", "sql", "rds", "relational", "postgres", "mysql", "nosql", "dynamodb", "cosmos", "aurora", "table"]),
        new("cache", "Cache", CloudSection,
            "M7 7h10v10H7z M9.5 3v4 M14.5 3v4 M9.5 17v4 M14.5 17v4 M3 9.5h4 M3 14.5h4 M17 9.5h4 M17 14.5h4 M10 10h4v4h-4z", false,
            ["redis", "memcached", "elasticache", "in-memory", "memory"]),
        new("objectstore", "Object storage", CloudSection,
            "M4 6a8 2.5 0 1 0 16 0a8 2.5 0 1 0-16 0l2 13.5a6 2 0 0 0 12 0L20 6", false,
            ["s3", "bucket", "blob", "blob storage", "cloud storage", "gcs", "storage"]),
        new("filestore", "File storage", CloudSection,
            "M3 7V5.5A1.5 1.5 0 0 1 4.5 4h4.8l2 2.2h8.2A1.5 1.5 0 0 1 21 7.7v10.8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z M3 9h18", false,
            ["file share", "nfs", "smb", "efs", "files", "shared drive", "folder"]),
        new("queue", "Queue", CloudSection,
            "M2 8h4v8H2z M7.5 8h4v8h-4z M13 8h4v8h-4z M18 12h4 M20 10l2 2-2 2", false,
            ["sqs", "message queue", "mq", "service bus", "rabbitmq", "fifo", "buffer"]),
        new("eventbus", "Event bus", CloudSection,
            "M3 6h18 M7 6v6 M12 6v9.5 M17 6v6" + C(7, 13.6, 1.6) + C(12, 17.1, 1.6) + C(17, 13.6, 1.6), false,
            ["topic", "pub/sub", "pubsub", "sns", "eventbridge", "event grid", "events", "notification", "publish", "subscribe"]),
        new("stream", "Stream", CloudSection,
            "M3 8c3-2.5 6 2.5 9 0s6-2.5 9 0 M3 12c3-2.5 6 2.5 9 0s6-2.5 9 0 M3 16c3-2.5 6 2.5 9 0s6-2.5 9 0", false,
            ["kafka", "kinesis", "event hub", "streaming", "data stream", "firehose", "log"]),
        new("warehouse", "Data warehouse", CloudSection,
            "M3 20V9l9-5 9 5v11z M7 20v-7h10v7 M7 16.5h10", false,
            ["dwh", "redshift", "bigquery", "synapse", "snowflake", "analytics", "data lake", "olap"]),
        new("monitoring", "Monitoring", CloudSection,
            "M3 4h18v13H3z M8 21h8 M12 17v4 M6 11h3l2-3 2 6 2-3h3", false,
            ["metrics", "logging", "observability", "cloudwatch", "alerts", "dashboard", "telemetry", "apm"]),
        new("identity", "Identity / key", CloudSection,
            C(8, 12, 3.5) + "M11.5 12H21 M18 12v3 M15 12v2", false,
            ["iam", "auth", "authentication", "sso", "key", "access", "active directory", "entra", "oauth", "role"]),
        new("vault", "Secret vault", CloudSection,
            "M4 4h16v15H4z M7 19v2 M17 19v2" + C(12, 11.5, 3.5) + "M12 8v1.2 M12 13.8V15 M15.5 11.5h2", false,
            ["secrets", "key vault", "kms", "secrets manager", "certificate", "hsm", "encryption"]),

        // ---- Network
        new("internet", "Internet", NetworkSection,
            C(12, 12, 9) + "M3 12h18 M12 3c3.2 3 3.2 15 0 18 M12 3c-3.2 3-3.2 15 0 18", false,
            ["web", "www", "public internet", "globe", "world"]),
        new("loadbalancer", "Load balancer", NetworkSection,
            C(5, 12, 2) + "M7 12h3 M10 12l7-6 M10 12h7 M10 12l7 6" + C(18.5, 5, 1.5) + C(18.5, 12, 1.5) + C(18.5, 19, 1.5), false,
            ["lb", "elb", "alb", "nlb", "balancer", "traffic manager", "front door", "reverse proxy", "proxy"]),
        new("cdn", "CDN", NetworkSection,
            C(12, 12, 3) + "M12 9V5 M12 15v4 M9 12H5 M15 12h4" + C(12, 3.4, 1.6) + C(12, 20.6, 1.6) + C(3.4, 12, 1.6) + C(20.6, 12, 1.6), false,
            ["content delivery", "cloudfront", "edge", "cache network", "akamai"]),
        new("dns", "DNS", NetworkSection,
            "M12 3v18 M12 5h7l2 2.5-2 2.5h-7 M12 12H5l-2 2.5L5 17h7 M9 21h6", false,
            ["domain", "route 53", "route53", "name server", "resolver", "hostname"]),
        new("firewall", "Firewall", NetworkSection,
            "M3 5h18v14H3z M3 9.7h18 M3 14.3h18 M9 5v4.7 M15 5v4.7 M6 9.7v4.6 M12 9.7v4.6 M18 9.7v4.6 M9 14.3V19 M15 14.3V19", false,
            ["waf", "security group", "nsg", "acl", "network security", "filter"]),
        new("vpn", "VPN / gateway", NetworkSection,
            "M12 3l8 3v6c0 5-3.5 8-8 9c-4.5-1-8-4-8-9V6z M8.5 12h7 M13 9.5l2.5 2.5-2.5 2.5", false,
            ["vpn", "gateway", "nat", "nat gateway", "tunnel", "ipsec", "direct connect", "expressroute", "interconnect"]),
        new("router", "Router", NetworkSection,
            C(12, 12, 9) + "M7 12h10 M14.5 9.5 17 12l-2.5 2.5 M12 7v10 M9.5 9.5 12 7l2.5 2.5", false,
            ["routing", "route table", "layer 3", "l3", "edge router"]),
        new("switch", "Switch", NetworkSection,
            "M3 6h18v12H3z M7 10h10 M14.5 8l2.5 2-2.5 2 M17 14H7 M9.5 12 7 14l2.5 2", false,
            ["network switch", "layer 2", "l2", "ethernet", "lan", "hub"]),
        new("wifi", "Wi-Fi access point", NetworkSection,
            "M2.5 9a14 14 0 0 1 19 0 M5.5 12.5a9.5 9.5 0 0 1 13 0 M8.5 16a5 5 0 0 1 7 0" + C(12, 19.5, 1.2), false,
            ["wifi", "wireless", "access point", "ap", "wlan", "hotspot"]),
        new("user", "User / client", NetworkSection,
            C(12, 8, 3.5) + "M5 20c0-4 3-6.5 7-6.5s7 2.5 7 6.5", false,
            ["person", "client", "customer", "actor", "people", "browser"]),
        new("mobile", "Mobile device", NetworkSection,
            "M7.5 3h9A1.5 1.5 0 0 1 18 4.5v15a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 6 19.5v-15A1.5 1.5 0 0 1 7.5 3z M11 18h2", false,
            ["phone", "smartphone", "tablet", "ios", "android", "app", "iot device"]),
        new("laptop", "Laptop", NetworkSection,
            "M5 5h14v10H5z M2.5 19h19l-2.5-4H5z", false,
            ["computer", "workstation", "desktop", "pc", "notebook", "client"]),
    ];

    private static readonly Dictionary<string, IconDef> BySubtype = All.ToDictionary(i => i.Subtype, StringComparer.Ordinal);
    private static readonly Dictionary<string, IconDef> ByKey = All.ToDictionary(i => i.Key, StringComparer.Ordinal);

    /// <summary>Shape subtypes of every icon, in palette order.</summary>
    public static readonly IReadOnlyList<string> Subtypes = All.Select(i => i.Subtype).ToList();

    /// <summary>The icon a shape subtype (<c>icon.database</c>) or a bare key (<c>database</c>) names, or null.</summary>
    public static IconDef? Find(string? subtypeOrKey) =>
        string.IsNullOrEmpty(subtypeOrKey) ? null
        : BySubtype.TryGetValue(subtypeOrKey, out var i) ? i
        : ByKey.GetValueOrDefault(subtypeOrKey);

    public static bool IsIcon(string? subtype) => subtype != null && subtype.StartsWith(Prefix, StringComparison.Ordinal) && BySubtype.ContainsKey(subtype);

    public static bool IsContainer(string? subtype) => Find(subtype) is { Container: true };

    /// <summary>The icon of a free shape, or of a deployment node that has one chosen.</summary>
    public static IconDef? Of(Element e) =>
        e.Kind == ElementKind.Shape ? (IsIcon(e.Get(FieldKeys.Subtype)) ? Find(e.Get(FieldKeys.Subtype)) : null)
        : Find(e.Get(IconField));

    public static (double W, double H) DefaultSize(IconDef icon) => icon.Container ? (320, 200) : (96, 96);

    /// <summary>Palette tiles per section ("Cloud", "Network").</summary>
    public static IReadOnlyList<PaletteItem> Items(string section) =>
        All.Where(i => i.Section == section).Select(i => new PaletteItem(ElementKind.Shape, i.Subtype, i.Name)).ToList();

    /// <summary>Extra shapes panel sections for a diagram type: the cloud and network libraries on whiteboards.</summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<PaletteItem> Items)> PaletteSections(DiagramType t) =>
        t == DiagramType.Whiteboard ? [(CloudSection, Items(CloudSection)), (NetworkSection, Items(NetworkSection))] : [];

    /// <summary>Shapes panel search: the tile's title, or for an icon any of its synonyms ("lambda", "s3", "vm").</summary>
    public static bool Matches(PaletteItem item, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        if (item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Kind != ElementKind.Shape || Find(item.Subtype) is not { } icon) return false;
        return icon.Synonyms.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase) || q.Contains(s, StringComparison.OrdinalIgnoreCase) && s.Length >= 3)
            || icon.Section.Equals(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Inspector fields added by the icon libraries.</summary>
    public static IEnumerable<string> EditableFields(ElementKind k)
    {
        if (k == ElementKind.Shape) yield return ProviderField;
        if (k is ElementKind.Node or ElementKind.Device or ElementKind.ExecutionEnvironment) yield return IconField;
    }

    public static string? Label(string key) => key switch
    {
        ProviderField => "Provider / product",
        IconField => "Icon",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        ProviderField => "Optional, shown under the name, e.g. AWS Lambda",
        _ => null,
    };

    /// <summary>
    /// The icon as SVG markup drawn into the square at (<paramref name="x"/>, <paramref name="y"/>) of side
    /// <paramref name="size"/>, stroked in <paramref name="color"/> (e.g. <c>currentColor</c>).
    /// </summary>
    public static string Markup(IconDef icon, double x, double y, double size, string color, double strokeWidth = 1.5)
    {
        var s = size / GridSize;
        return string.Create(CultureInfo.InvariantCulture,
            $"<path class=\"icon-glyph\" data-icon=\"{icon.Key}\" transform=\"translate({x:0.##},{y:0.##}) scale({s:0.####})\" d=\"{icon.Path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{strokeWidth:0.##}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
    }

    /// <summary>A standalone SVG document of the icon (draw.io export, thumbnails).</summary>
    public static string Svg(IconDef icon, string color, double size = 48) => string.Create(CultureInfo.InvariantCulture,
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size:0.##}\" height=\"{size:0.##}\" viewBox=\"0 0 24 24\">{Markup(icon, 0, 0, GridSize, color)}</svg>");
}
