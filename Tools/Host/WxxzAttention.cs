using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceSoul2.Host;

/// <summary>Names and a short interaction lease; separate from Soul's thoughts, sleep and durable attention.</summary>
public sealed class WxxzAttention
{
    public const int FocusSeconds = 30;
    public sealed class Settings
    {
        public bool enabled { get; set; } = true;
        public string name { get; set; } = "";
        public bool phonetic { get; set; } = true;
        public string pronunciation { get; set; } = "";
        public string[] aliases { get; set; }
        public string reply { get; set; } = "诶，我在。";
    }
    public sealed record Correction(string Text, string RawText, bool Changed, bool Mentioned, bool WakeOnly,
        string Pronunciation, string MatchMode);
    public sealed record Decision(bool acknowledge, bool admit, string outcome, string reason);
    public sealed record Snapshot(string state,long focusRemainingMs,int focusSeconds);
    public static Decision Decide(Correction words, bool wake, bool focused, bool owner)
    {
        if(string.IsNullOrWhiteSpace(words.Text)) return new(false,false,"no_text","未得到听写文字，不进入Moment。");
        if(wake && (!focused || words.WakeOnly))
        {
            if(!words.Mentioned) return new(false,false,"standby","发呆中未命中名字，不进入Moment。");
            if(!owner || words.WakeOnly) return new(true,false,"wake_ack",
                !owner ? "名字命中，仅简短应答；声纹未确认，不将本句交给主模型。" : "仅叫名字，简短应答后等待后续指令，不创建对话Moment。");
            return new(true,true,"verified","名字连同实际指令，先简短应答，再交给主逻辑。");
        }
        return owner ? new(false,true,"verified","本人声纹确认，允许本句交给主逻辑。")
            : new(false,false,"speaker_unconfirmed","声纹未确认是本人，本句不进入Moment。");
    }
    public object Preview(string text, bool focused, string speaker)
    {
        if(speaker is not ("owner" or "unknown" or "short")) throw new ArgumentException("请选择模拟本人、陌生声音或短名字。");
        var config=Configuration();
        var words=Correct(text);
        var decision=Decide(words,config.enabled,focused,speaker=="owner");
        return new { simulated=true, correction=words, decision, wouldCreateMoment=decision.admit,
            wouldAcknowledge=decision.acknowledge,
            nextState=decision.acknowledge || decision.admit || focused ? "focused" : "standby",
            focusSeconds=FocusSeconds, speaker,
            note="纯文字沙盘：未采音、未验证真实声纹、不播放、不写入Moment、不调用主模型。" };
    }
    private readonly string path;
    private readonly Func<string> defaultName;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new();
    private Settings settings;
    private bool listening, processing;
    private DateTimeOffset until;
    public WxxzAttention(string path, Func<string> defaultName, Func<DateTimeOffset> clock = null)
    {
        this.path = path; this.defaultName = defaultName; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        settings = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) : new Settings();
        Validate(settings, allowDefault:true);
    }
    public Settings Configuration()
    {
        lock(sync)
        {
            var name = string.IsNullOrWhiteSpace(settings.name) ? defaultName() : settings.name;
            return new Settings { enabled = settings.enabled, name = name,
                phonetic = settings.phonetic, pronunciation = Reading(name,settings.pronunciation),
                aliases = settings.aliases?.ToArray() ?? (name == "阿循" ? new[] { "阿旭" } : Array.Empty<string>()), reply = settings.reply };
        }
    }
    private static void Validate(Settings value, bool allowDefault = false)
    {
        bool Word(string text) => text != null && text.Length is > 0 and <= 32 &&
            !text.Any(char.IsWhiteSpace) && !text.Any(char.IsControl) && !text.Any(char.IsPunctuation);
        if (value == null || !(allowDefault && value.name == "" || Word(value.name)) ||
            value.aliases?.Length > 16 || value.aliases?.Any(x => !Word(x)) == true ||
            value.pronunciation?.Length > 256 || !ValidReading(value.pronunciation) ||
            string.IsNullOrWhiteSpace(value.reply) || value.reply.Length > 30 || value.reply.Any(char.IsControl))
            throw new ArgumentException("请填写1～32字的规范名字、空格分隔的拼音（最多32个音节）、最多16个特殊变体，以及30字内的简短应答。");
    }
    public void Save(Settings value)
    {
        Validate(value);
        var copy = new Settings { enabled = value.enabled, name = value.name.Trim(),
            phonetic = value.phonetic, pronunciation = NormalizeReading(value.pronunciation),
            aliases = (value.aliases ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !x.Equals(value.name,StringComparison.OrdinalIgnoreCase)).ToArray(), reply = value.reply.Trim() };
        lock(sync)
        {
            if (listening) throw new InvalidOperationException("请先暂停名字监听，再修改称呼。");
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(copy)); File.Move(temporary,path,true); }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
            }
            settings = copy;
        }
    }
    private static string NormalizeReading(string value)
    {
        var decomposed=Regex.Replace((value ?? "").Trim().ToLowerInvariant().Replace("u:","v"),"[üǖǘǚǜ]","v").Normalize(NormalizationForm.FormD);
        var plain=new string(decomposed.Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(Regex.Replace(plain,"[1-5]"," "),"\\s+"," ").Trim();
    }
    private static bool ValidReading(string value)
    {
        if(string.IsNullOrWhiteSpace(value)) return true;
        var normalized=NormalizeReading(value);
        return Regex.IsMatch(normalized,"^[a-z]{1,6}(?: [a-z]{1,6}){0,31}$");
    }
    private static string Reading(string name,string configured)
    {
        if(!string.IsNullOrWhiteSpace(configured)) return NormalizeReading(configured);
        return !string.IsNullOrEmpty(name) && name.All(TinyPinyin.PinyinHelper.IsChinese)
            ? TinyPinyin.PinyinHelper.GetPinyin(name).ToLowerInvariant().Replace("ü","v") : "";
    }
    private static int PhoneticLength(string raw,int start,string[] syllables)
    {
        var cursor=start;
        for(var part=0;part<syllables.Length;part++)
        {
            var syllable=syllables[part];
            if(cursor>=raw.Length || !TinyPinyin.PinyinHelper.IsChinese(raw[cursor]) ||
                !string.Equals(TinyPinyin.PinyinHelper.GetPinyin(raw[cursor]).Replace("Ü","V"),syllable,StringComparison.OrdinalIgnoreCase)) return 0;
            cursor++;
            // ASR sometimes inserts spaces inside a name; punctuation still ends it.
            if(part<syllables.Length-1) while(cursor<raw.Length && char.IsWhiteSpace(raw[cursor])) cursor++;
        }
        return cursor-start;
    }
    private static bool Boundary(string text, int index, int length, string term)
    {
        bool Latin(char c) => c <= 127 && (char.IsLetterOrDigit(c) || c == '_');
        return !(Latin(term[0]) && index > 0 && Latin(text[index-1])) &&
            !(Latin(term[^1]) && index+length < text.Length && Latin(text[index+length]));
    }
    private static bool Address(string text, int index, int length)
    {
        var before = text[..index]; var after = text[(index+length)..];
        if (Regex.IsMatch(before, "(?:一条|这条|那条|河边|河里的|沿着|跨过|流进|流出)\\s*$") ||
            Regex.IsMatch(after, "^(?:里|中|边|旁|流水|流淌|流过|水很|很清澈|真清澈)")) return false;
        if (after.Length == 0 || char.IsPunctuation(after[0]) || char.IsWhiteSpace(after[0]))
        {
            var greeting = Regex.Replace(before, "[\\s\\p{P}]", "");
            if (greeting is "" or "你好" or "喂" or "嘿" or "嗨" or "哈喽" or "请问" or "诶" or "哎" or "嗯") return true;
            if (before.Length > 0 && (char.IsPunctuation(before[^1]) || char.IsWhiteSpace(before[^1]))) return true;
            return Regex.IsMatch(before, "(?:叫|喊|名字是|你是|称呼你)\\s*$");
        }
        return Regex.IsMatch(after, "^(?:你|在吗|在不在|帮|给我|请|能不能|能帮|我|我们|你好|早上好|晚上好|听我|醒醒|看一下|说一下|别|先|今天|明天|现在|告诉我|为什么|怎么|记住|把)");
    }
    public Correction Correct(string raw)
    {
        raw ??= "";
        if (raw.Length > 4000) throw new ArgumentException("听写文本超过4000字。");
        var config = Configuration();
        var syllables=config.phonetic && config.pronunciation.Length>0 ? config.pronunciation.Split(' ') : Array.Empty<string>();
        var aliases=config.aliases.OrderByDescending(x=>x.Length).ToArray();
        var latin=syllables.Length==0 ? null : new Regex("\\G"+string.Join("\\s+",syllables.Select(Regex.Escape)),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        var output=new StringBuilder(); var mode="none";
        for(var index=0;index<raw.Length;)
        {
            var length=0; var bySound=false;
            if(syllables.Length>0)
            {
                length=PhoneticLength(raw,index,syllables);
                if(length==0 && latin.IsMatch(raw,index))
                {
                    var match=latin.Match(raw,index);
                    if(Boundary(raw,index,match.Length,match.Value)) length=match.Length;
                }
                if(length>0 && Address(raw,index,length)) bySound=true; else length=0;
            }
            if(length==0)
                foreach(var alias in aliases)
                    if(raw.AsSpan(index).StartsWith(alias,StringComparison.OrdinalIgnoreCase) &&
                        Boundary(raw,index,alias.Length,alias) && Address(raw,index,alias.Length)) {length=alias.Length;break;}
            if(length==0) {output.Append(raw[index++]);continue;}
            output.Append(config.name); index+=length;
            if(bySound) mode="phonetic"; else if(mode!="phonetic") mode="alias";
        }
        var text=output.ToString();
        if (text.Length > 4000) throw new ArgumentException("名字校正后文本超过4000字，未截断。");
        var mentioned = !string.IsNullOrWhiteSpace(config.name) && Regex.Matches(text, Regex.Escape(config.name), RegexOptions.IgnoreCase)
            .Any(m => Boundary(text, m.Index, m.Length, config.name));
        var rest = mentioned ? Regex.Replace(text, Regex.Escape(config.name), "", RegexOptions.IgnoreCase) : text;
        rest = Regex.Replace(rest, "[\\s\\p{P}]", "");
        var only = mentioned && (rest is "" or "你好" or "在吗" or "你在吗" or "在不在" or "你在不在" or "喂" or "嘿" or "醒醒");
        return new Correction(text,raw,text != raw,mentioned,only,config.pronunciation,mode=="none" && mentioned ? "literal" : mode);
    }
    public void Start() { lock(sync) { listening=true; processing=false; until=default; } }
    public void Stop() { lock(sync) { listening=false; processing=false; until=default; } }
    public bool FocusedAt(DateTimeOffset observed) { lock(sync) return listening && (processing || observed < until); }
    public void Busy() { lock(sync) processing=true; }
    public void Complete() { lock(sync) { processing=false; if(listening) until=clock().AddSeconds(FocusSeconds); } }
    public Snapshot State()
    {
        lock(sync)
        {
            var now=clock();
            return new Snapshot(!listening ? "stopped" : processing ? "responding" : now < until ? "focused" : "standby",
                !listening || processing ? 0 : Math.Max(0,(long)(until-now).TotalMilliseconds),FocusSeconds);
        }
    }
    public DateTimeOffset Now => clock();
}
