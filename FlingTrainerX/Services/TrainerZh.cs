using System.IO;
using System.Text;

namespace FlingTrainerX.Services;

/// <summary>
/// 修改选项英译中。主表是 Assets/option-zh.tsv（官网现有 800 多条选项已全部收录），
/// 新抓到的条目先用「动词 + 名词」规则拼，拼不出来再原样返回英文，绝不返回半中半英的乱码。
/// </summary>
public static class TrainerZh
{
    private static readonly Dictionary<string, string> Table = Load();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static string TableFile =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "option-zh.tsv");

    public static bool IsReady => Table.Count > 0;

    /// <summary>翻译一条英文（选项名 / 分组标题）。查不到且拼不出时返回原文。</summary>
    public static string Of(string? en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";

        var key = Normalize(en);
        if (key.Length == 0) return en;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        var zh = Lookup(key) ?? Compose(key) ?? en.Trim();

        lock (Cache)
        {
            if (Cache.Count < 4000) Cache[key] = zh;
        }

        return zh;
    }

    private static string? Lookup(string key) =>
        Table.TryGetValue(key, out var zh) ? zh : null;

    private static readonly Dictionary<string, string> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = "Steam",
        ["Epic Games Store"] = "Epic 商店",
        ["GOG"] = "GOG",
        ["Xbox/Game Pass"] = "Xbox / Game Pass",
        ["EA App"] = "EA App",
        ["Ubisoft Connect"] = "育碧 Connect",
        ["Battle.net"] = "战网"
    };

    /// <summary>平台名中文化，未知平台原样返回。</summary>
    public static string Platform(string? en) =>
        string.IsNullOrWhiteSpace(en)
            ? ""
            : (Platforms.TryGetValue(en.Trim(), out var zh) ? zh : en.Trim());

    /// <summary>规则拼接：Set / Edit / Infinite / Multiplier … 覆盖官网后续新增的常用写法。</summary>
    private static string? Compose(string key)
    {
        // 已经是中文（官网偶尔会有），直接返回
        if (!HasLatin(key)) return key;

        foreach (var (prefix, zhPrefix) in Prefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = key[prefix.Length..].Trim();
            if (Noun(rest) is { } n1) return zhPrefix + n1;
        }

        foreach (var (suffix, zhSuffix) in Suffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var rest = key[..^suffix.Length].Trim();
            if (Noun(rest) is { } n2) return n2 + zhSuffix;
        }

        return Noun(key);
    }

    /// <summary>名词短语：先查整段，再逐词拼；有一个词不认识就放弃。</summary>
    private static string? Noun(string phrase)
    {
        phrase = phrase.Trim(' ', ':', '-');
        if (phrase.Length == 0) return null;

        if (Nouns.TryGetValue(phrase, out var whole)) return whole;

        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return null;

        var sb = new StringBuilder();
        foreach (var w in words)
        {
            var token = w.Trim(',', '.', ':', '(', ')');
            if (token.Length == 0) continue;
            if (!Nouns.TryGetValue(token, out var t)) return null;
            sb.Append(t);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static bool HasLatin(string s)
    {
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') return true;
        }

        return false;
    }

    /// <summary>统一大小写、弯引号、多余空白，让对照表匹配更稳。</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;

        foreach (var raw in s.Trim())
        {
            var c = raw switch
            {
                '\u2019' or '\u2018' => '\'',
                '\u201c' or '\u201d' => '"',
                '\u00a0' => ' ',
                _ => raw
            };

            if (char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
            space = false;
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(TableFile)) return map;

            foreach (var line in File.ReadAllLines(TableFile, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '/') continue;

                var cut = line.IndexOf('\t');
                if (cut <= 0 || cut + 1 >= line.Length) continue;

                var en = Normalize(line[..cut]);
                var zh = line[(cut + 1)..].Trim();
                if (en.Length == 0 || zh.Length == 0) continue;

                map[en] = zh;
            }
        }
        catch
        {
            // 读不到对照表时退化为规则翻译
        }

        return map;
    }

    private static readonly (string Prefix, string Zh)[] Prefixes =
    {
        ("set ", "设置"), ("edit ", "修改"), ("infinite ", "无限"), ("max ", "上限："),
        ("no ", "无"), ("freeze ", "锁定"), ("fast ", "快速"), ("unlock all ", "解锁全部"),
        ("obtain all ", "获得全部"), ("teleport to ", "传送至"), ("increase ", "提升"),
        ("super ", "超级"), ("instant ", "瞬间"), ("zero ", "归零："), ("ignore ", "忽略"),
        ("drain ", "削减"), ("restore ", "补满"), ("one hit ", "一击"), ("one turn ", "一回合"),
        ("quick ", "快速"), ("auto ", "自动")
    };

    private static readonly (string Suffix, string Zh)[] Suffixes =
    {
        (" multiplier", "倍率"), (" speed", "速度"), (" rate", "率"), (" chance", "几率"),
        (" count", "数量"), (" bonus", "加成"), (" cooldown", "冷却"), (" won't decrease", "不减少"),
        (" duration", "持续时间"), (" radius", "范围"), (" level", "等级"), (" amount", "数量"),
        (" damage", "伤害"), (" defense", "防御"), (" resistance", "抗性")
    };

    /// <summary>规则翻译用的名词表，只收高频游戏词，宁缺毋滥。</summary>
    private static readonly Dictionary<string, string> Nouns = new(StringComparer.Ordinal)
    {
        ["health"] = "生命", ["hp"] = "生命", ["mp"] = "法力", ["mana"] = "法力",
        ["stamina"] = "体力", ["energy"] = "精力", ["focus"] = "专注", ["spirit"] = "灵力",
        ["ammo"] = "弹药", ["reload"] = "装填", ["money"] = "金钱", ["gold"] = "金币",
        ["credit"] = "信用点", ["credits"] = "信用点", ["exp"] = "经验", ["xp"] = "经验值",
        ["level"] = "等级", ["damage"] = "伤害", ["defense"] = "防御", ["armor"] = "护甲",
        ["shield"] = "护盾", ["speed"] = "速度", ["time"] = "时间", ["items"] = "物品",
        ["item"] = "物品", ["weapon"] = "武器", ["weapons"] = "武器", ["durability"] = "耐久",
        ["player"] = "玩家", ["character"] = "角色", ["companion"] = "同伴", ["enemy"] = "敌人",
        ["enemies"] = "敌人", ["unit"] = "单位", ["units"] = "单位", ["army"] = "军队",
        ["troops"] = "兵力", ["morale"] = "士气", ["move"] = "移动", ["movement"] = "移动",
        ["jump"] = "跳跃", ["fly"] = "飞行", ["gravity"] = "重力", ["weight"] = "负重",
        ["skill"] = "技能", ["skills"] = "技能", ["skill points"] = "技能点",
        ["attribute points"] = "属性点", ["talent points"] = "天赋点", ["perk points"] = "专长点数",
        ["research"] = "研究", ["construction"] = "建造", ["crafting"] = "制作",
        ["accuracy"] = "命中", ["critical"] = "暴击", ["dodge"] = "闪避", ["luck"] = "幸运",
        ["recoil"] = "后坐力", ["overheat"] = "过热", ["cooldown"] = "冷却",
        ["fuel"] = "燃料", ["oxygen"] = "氧气", ["food"] = "食物", ["water"] = "水",
        ["hunger"] = "饥饿", ["injury"] = "受伤", ["bleeding"] = "流血", ["radiation"] = "辐射",
        ["temperature"] = "温度", ["weather"] = "天气", ["daytime"] = "白天",
        ["location"] = "位置", ["waypoint"] = "路径点", ["marker"] = "标记",
        ["stealth"] = "潜行", ["vision"] = "视野", ["fov"] = "视野角度",
        ["strength"] = "力量", ["vitality"] = "活力", ["dexterity"] = "敏捷",
        ["intelligence"] = "智力", ["charisma"] = "魅力", ["reputation"] = "声望",
        ["experience"] = "经验值", ["purchase"] = "购买", ["price"] = "价格",
        ["production"] = "生产", ["storage"] = "仓库", ["capacity"] = "容量",
        ["build"] = "建造", ["buildings"] = "建筑", ["population"] = "人口"
    };
}
