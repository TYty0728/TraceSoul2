using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Host;

public sealed class ExpressionLibrary : IExpressionLibrary
{
    private const double SemanticCandidateWindow=0.04;
    private sealed record Tag(string Name,string Category);
    private sealed record Asset(string Id,string Name,string Path,string Description,string[] Legacy,Dictionary<string,double> Manual,string Hash,string Index,string[] Semantics);
    private sealed record Semantic(string Name,string Cue,bool Required,string[] Exclusions);
    private readonly List<Semantic> semantics=new();
    private readonly Dictionary<string,string> indexHashes=new();
    private readonly string palettePath,dataRoot,packageRoot,semanticPath;
    private readonly double semanticThreshold;
    private readonly Func<IEmbeddingService> embedding;
    private readonly SemaphoreSlim scoring = new(1,1);
    private readonly object sync = new();
    private readonly List<Tag> tags = new();
    private readonly List<Asset> assets = new();
    private readonly double threshold;
    private readonly int topTags;
    private readonly string revision;
    private string profileModel;
    private float[][] anchors;
    private float[][] semanticAnchors;
    private float[][][] semanticExclusions;
    private List<EmotionTagScore>[] profiles;
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string text)=>Hash(Encoding.UTF8.GetBytes(text));
    private static JsonDocument JsonFile(string path,int limit=2*1024*1024)
    {
        if(new FileInfo(path).Length>limit)throw new InvalidOperationException("表情库索引文件过大。");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
    private static bool SafeFile(string path) => File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0;
    private static string String(JsonElement root,string key)=>root.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():"";
    public ExpressionLibrary(string palettePath,string dataRoot,string packageRoot,Func<IEmbeddingService> embedding,string semanticPath=null)
        :this(palettePath,dataRoot,packageRoot,embedding,semanticPath,null){}
    private ExpressionLibrary(string palettePath,string dataRoot,string packageRoot,Func<IEmbeddingService> embedding,string semanticPath,Dictionary<string,byte[]> overrides)
    {
        this.embedding=embedding;
        this.palettePath=palettePath;this.dataRoot=dataRoot;this.packageRoot=packageRoot;
        this.semanticPath=semanticPath??Path.Combine(Path.GetDirectoryName(palettePath),"expression_semantics.json");
        if(File.Exists(this.semanticPath))
        {
            using var vocabulary=JsonFile(this.semanticPath);
            if(vocabulary.RootElement.GetProperty("schema_version").GetInt32()!=1)throw new InvalidOperationException("不支持的表情语义词表版本。");
            foreach(var item in vocabulary.RootElement.GetProperty("tags").EnumerateArray())
            {
                var name=String(item,"tag");ValidateSemanticTags(new[]{name});
                var cue=String(item,"description");
                if(cue.Length>1000||semantics.Any(s=>s.Name==name))throw new InvalidOperationException("表情语义词表无效或重复。");
                var exclusions=item.TryGetProperty("exclusions",out var denied)?denied.EnumerateArray().Select(v=>v.GetString()).ToArray():Array.Empty<string>();
                if(exclusions.Length>4||exclusions.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>500))throw new InvalidOperationException("语义排除样例最多4条，每条1～500字。");
                semantics.Add(new(name,name+"。"+cue,item.TryGetProperty("requires_intent",out var required)&&required.GetBoolean(),exclusions));
            }
        }
        using(var palette=JsonFile(palettePath))
        {
            if(palette.RootElement.GetProperty("schema_version").GetInt32()!=1)throw new InvalidOperationException("不支持的情绪词表版本。");
            foreach(var category in palette.RootElement.GetProperty("categories").EnumerateArray())
                foreach(var label in category.GetProperty("tags").EnumerateArray())
                {
                    var name=label.GetString();var group=category.GetProperty("name").GetString();
                    if(string.IsNullOrWhiteSpace(name)||name.Length>32||string.IsNullOrWhiteSpace(group)||tags.Any(t=>t.Name==name))
                        throw new InvalidOperationException("情绪词表包含无效或重复标签。");
                    tags.Add(new(name,group));
                }
        }
        if(tags.Count is <1 or >128)throw new InvalidOperationException("情绪标签数量应为1～128。");
        // Retain zero-valued DTO fields for older clients; legacy thresholds no
        // longer control scoring or selection.
        string[] personas={"_default","default"};threshold=0;topTags=Math.Min(4,tags.Count);semanticThreshold=0;
        var config=Path.Combine(dataRoot,"config.json");
        if(File.Exists(config))
        {
            using var doc=JsonFile(config,65536);var root=doc.RootElement;
            if(root.TryGetProperty("personas",out var p)&&p.ValueKind==JsonValueKind.Array)
                personas=p.EnumerateArray().Select(v=>v.GetString()).ToArray();
            if(root.TryGetProperty("top_tag_count",out var n))topTags=n.GetInt32();
        }
        if(topTags<1||topTags>tags.Count)throw new InvalidOperationException("参与匹配的标签数量无效。");
        var byName=new Dictionary<string,Asset>(StringComparer.OrdinalIgnoreCase);
        // Package defaults first, then persistent library. Persona order intentionally overrides duplicate filenames.
        foreach(var root in new[]{packageRoot,dataRoot}.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach(var persona in personas)
        {
            if(string.IsNullOrWhiteSpace(persona)||persona.Length>80||persona is "." or ".."||persona.IndexOfAny(new[]{'/','\\',':'})>=0)
                throw new InvalidOperationException("表情人格目录无效。");
            var parent=Path.GetFullPath(Path.Combine(root,"emojis"));
            var folder=Path.Combine(parent,persona);
            if(!Directory.Exists(folder))continue;
            if((File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("表情目录不能重定向到其他位置。");
            var index=Path.Combine(folder,"emoji_index.json");
            if(!SafeFile(index))continue;
            var indexBytes=overrides!=null&&overrides.TryGetValue(index,out var replacement)?replacement:File.ReadAllBytes(index);
            if(indexBytes.Length>2*1024*1024)throw new InvalidOperationException("表情索引文件过大。");
            indexHashes[index]=Hash(indexBytes);
            using var doc=JsonDocument.Parse(indexBytes);
            foreach(var entry in doc.RootElement.EnumerateObject())
            {
                if(entry.Name!=Path.GetFileName(entry.Name)||entry.Name.IndexOfAny(new[]{'/','\\',':'})>=0)throw new InvalidOperationException("表情文件名无效。");
                var path=Path.GetFullPath(Path.Combine(folder,entry.Name));
                var extension=Path.GetExtension(path).ToLowerInvariant();
                if(extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"))continue;
                if(!SafeFile(path)||new FileInfo(path).Length is <1 or >4*1024*1024)continue;
                var description=String(entry.Value,"description");
                if(description.Length>2000)throw new InvalidOperationException("表情描述过长。");
                var legacy=entry.Value.TryGetProperty("emotions",out var emotions)&&emotions.ValueKind==JsonValueKind.Array
                    ?emotions.EnumerateArray().Select(v=>v.GetString()).Where(v=>!string.IsNullOrWhiteSpace(v)).ToArray():Array.Empty<string>();
                if(legacy.Length>128||legacy.Any(s=>s.Length>80))throw new InvalidOperationException("原始表情标签过长。");
                var manual=new Dictionary<string,double>(StringComparer.Ordinal);
                var semanticTags=entry.Value.TryGetProperty("semantic_tags",out var semanticLabels)
                    ?semanticLabels.EnumerateArray().Select(v=>v.GetString()).ToArray():Array.Empty<string>();
                ValidateSemanticTags(semanticTags);
                if(entry.Value.TryGetProperty("tag_scores",out var scores))
                    foreach(var pair in scores.EnumerateObject())
                    {
                        var score=pair.Value.GetDouble();
                        if(!tags.Any(t=>t.Name==pair.Name)||!double.IsFinite(score)||score<0||score>1)throw new InvalidOperationException("表情标签评分应来自统一词表，且介于0和1之间。");
                        manual.Add(pair.Name,score);
                    }
                var hash=Hash(File.ReadAllBytes(path));
                byName[entry.Name]=new(Hash(persona+"/"+entry.Name+"/"+hash),entry.Name,path,description,legacy,manual,hash,index,semanticTags);
                if(byName.Count>2000)throw new InvalidOperationException("表情库最多2000张。");
            }
        }
        assets=byName.Values.OrderBy(a=>a.Id,StringComparer.Ordinal).ToList();
        foreach(var label in assets.SelectMany(a=>a.Semantics).Distinct(StringComparer.Ordinal))
            if(!semantics.Any(s=>s.Name==label))semantics.Add(new(label,label,true,Array.Empty<string>()));
        if(semantics.Count>256)throw new InvalidOperationException("表情语义标签最多256个。");
        revision=Hash(JsonSerializer.Serialize(new{matching="semantic_first",semanticCandidateWindow=SemanticCandidateWindow,tags,semantics,assets=assets.Select(a=>new{a.Id,a.Description,a.Legacy,a.Manual,a.Hash,a.Semantics}),threshold,topTags,semanticThreshold}));
    }
    public ExpressionCatalog Catalog()
    {
        lock(sync)return new(){revision=revision,model=profileModel??embedding()?.ModelId,threshold=threshold,topTags=topTags,profilesReady=profiles!=null,
            tags=tags.Select(t=>new EmotionTagScore{tag=t.Name,category=t.Category}).ToList(),
            assets=assets.Select((a,i)=>new ExpressionAsset{id=a.Id,name=a.Name,legacyTags=(string[])a.Legacy.Clone(),
                tags=profiles==null?new():Clone(profiles[i]),semanticTags=(string[])a.Semantics.Clone(),requiresSemanticMatch=RequiresSemantic(a)}).ToList(),
            semanticTags=semantics.Select(s=>s.Name).ToArray(),semanticThreshold=semanticThreshold};
    }
    private bool RequiresSemantic(Asset asset)=>asset.Semantics.Any(label=>semantics.Any(s=>s.Name==label&&s.Required));
    private static void ValidateSemanticTags(string[] labels)
    {
        if(labels==null||labels.Length>16||labels.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>32||s!=s.Trim()||s.Any(char.IsControl))||labels.Distinct(StringComparer.Ordinal).Count()!=labels.Length)
            throw new ArgumentException("每张图最多16个语义标签，每项1～32字、不能重复或换行。");
    }
    // Construct and validate the replacement before atomically changing this one persistent index.
    public ExpressionLibrary WithSemanticTags(string id,string[] labels)
    {
        ValidateSemanticTags(labels);ReadImage(id);
        var asset=assets.Single(a=>a.Id==id);
        var allowed=Path.GetFullPath(Path.Combine(dataRoot,"emojis"))+Path.DirectorySeparatorChar;
        if(!asset.Index.StartsWith(allowed,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("请先把包内默认图库复制到持久图库，再编辑用途标签。");
        if(!SafeFile(asset.Index))throw new InvalidOperationException("图片索引已改变，请重新加载图库。");
        var original=File.ReadAllBytes(asset.Index);
        if(Hash(original)!=indexHashes[asset.Index])throw new InvalidOperationException("索引已被其他操作修改，请重新加载后再保存。");
        var document=JsonNode.Parse(original).AsObject();
        document[asset.Name].AsObject()["semantic_tags"]=JsonSerializer.SerializeToNode(labels);
        var bytes=Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        var next=new ExpressionLibrary(palettePath,dataRoot,packageRoot,embedding,semanticPath,new(){{asset.Index,bytes}});
        var temp=asset.Index+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllBytes(temp,bytes);
            if(!SafeFile(asset.Index)||Hash(File.ReadAllBytes(asset.Index))!=Hash(original))throw new InvalidOperationException("索引已被其他操作修改，请重新加载后再保存。");
            File.Replace(temp,asset.Index,asset.Index+".semantic-"+Guid.NewGuid().ToString("N")+".bak");
            return next;
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    private static List<EmotionTagScore> Clone(IEnumerable<EmotionTagScore> values)=>values.Select(t=>new EmotionTagScore{tag=t.tag,category=t.category,score=t.score,similarity=t.similarity}).ToList();
    public ExpressionImage ReadImage(string id)
    {
        var asset=assets.FirstOrDefault(a=>a.Id==id)??throw new ArgumentException("表情图片不存在。");
        // Revalidate at use time. A selection must never resolve to a newly replaced file or link.
        if(!SafeFile(asset.Path)||(File.GetAttributes(Path.GetDirectoryName(asset.Path))&FileAttributes.ReparsePoint)!=0||
            (File.GetAttributes(Path.GetDirectoryName(Path.GetDirectoryName(asset.Path)))&FileAttributes.ReparsePoint)!=0||new FileInfo(asset.Path).Length>4*1024*1024)
            throw new InvalidOperationException("表情图片已改变，请重新加载图库。");
        var bytes=File.ReadAllBytes(asset.Path);
        if(Hash(bytes)!=asset.Hash)throw new InvalidOperationException("表情图片已改变，请重新加载图库。");
        return new(){bytes=bytes,path=asset.Path,contentType=Path.GetExtension(asset.Path).ToLowerInvariant() switch{
            ".jpg" or ".jpeg"=>"image/jpeg",".webp"=>"image/webp",".gif"=>"image/gif",_=>"image/png"}};
    }
    private static double Cos(float[] a,float[] b)
    {
        if(a==null||b==null||a.Length==0||a.Length!=b.Length||a.Any(x=>!float.IsFinite(x))||b.Any(x=>!float.IsFinite(x)))
            throw new InvalidOperationException("表情语义向量无效。");
        double sum=0,aa=0,bb=0;for(var i=0;i<a.Length;i++){sum+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}
        if(aa<=0||bb<=0)throw new InvalidOperationException("表情语义向量为空。");
        return Math.Clamp(sum/Math.Sqrt(aa*bb),-1,1);
    }
    private List<EmotionTagScore> Profile(float[] vector,float[][] tagVectors)
    {
        var similarities=tagVectors.Select(v=>Cos(v,vector)).ToArray();
        var sorted=similarities.OrderBy(x=>x).ToArray();
        var median=sorted[sorted.Length/2];
        return tags.Select((tag,i)=>new EmotionTagScore{tag=tag.Name,category=tag.Category,similarity=similarities[i],
            score=Math.Clamp((similarities[i]-median)/Math.Max(0.05,1-median),0,1)}).ToList();
    }
    private async Task EnsureProfiles(IEmbeddingService encoder,CancellationToken token)
    {
        if(this.profiles!=null&&profileModel==encoder.ModelId)return;
        var vectors=new List<float[]>();
        foreach(var tag in tags)vectors.Add(await encoder.EmbedAsync(tag.Name,token));
        var anchors=vectors.ToArray();var profiles=new List<EmotionTagScore>[assets.Count];
        var semanticVectors=new List<float[]>();
        foreach(var semantic in semantics)semanticVectors.Add(await encoder.EmbedAsync(semantic.Cue,token));
        var excludedVectors=new List<float[][]>();
        foreach(var semantic in semantics)
        {
            var examples=new List<float[]>();
            foreach(var text in semantic.Exclusions)examples.Add(await encoder.EmbedAsync(text,token));
            excludedVectors.Add(examples.ToArray());
        }
        for(var i=0;i<assets.Count;i++)
        {
            var asset=assets[i];var cue=string.Join("、",asset.Legacy)+"。"+asset.Description;
            profiles[i]=Profile(await encoder.EmbedAsync(cue,token),anchors);
            foreach(var tag in profiles[i])
                if(asset.Manual.TryGetValue(tag.tag,out var score))tag.score=score;
                else if(asset.Legacy.Contains(tag.tag,StringComparer.Ordinal))tag.score=1;
        }
        token.ThrowIfCancellationRequested();
        lock(sync){this.anchors=anchors;this.semanticAnchors=semanticVectors.ToArray();this.semanticExclusions=excludedVectors.ToArray();this.profiles=profiles;profileModel=encoder.ModelId;}
    }
    public async Task<ExpressionMatch> MatchAsync(ExpressionQuery query,CancellationToken token)
    {
        if(query==null||query.text==null||query.mood==null||query.text.Length>2000||query.mood.Length>500)
            throw new ArgumentException("表情预览正文最多2000字，感受最多500字。");
        var n=query.topTags??topTags;
        if(n<1||n>tags.Count)throw new ArgumentException("参与匹配的标签数量超出词表范围。");
        var result=new ExpressionMatch{status="skipped",reason="no_match",revision=revision,threshold=threshold,topTags=n,semanticThreshold=semanticThreshold};
        if(string.IsNullOrWhiteSpace(query.text)&&string.IsNullOrWhiteSpace(query.mood)){result.reason="empty_expression";return result;}
        if(assets.Count==0){result.reason="empty_library";return result;}
        var encoder=embedding();
        if(encoder==null){result.reason="embedding_unavailable";return result;}
        await scoring.WaitAsync(token);
        try
        {
            await EnsureProfiles(encoder,token);
            var text=string.IsNullOrWhiteSpace(query.text)?query.mood:query.text;
            var vector=await encoder.EmbedAsync(text,token);
            if(!string.IsNullOrWhiteSpace(query.text))
                result.semanticTags=semantics.Select((s,i)=>{
                    var similarity=Math.Max(0,Cos(vector,semanticAnchors[i]));
                    var denied=semanticExclusions[i].Select(v=>Cos(vector,v)).DefaultIfEmpty(0).Max();
                    return new SemanticTagScore{tag=s.Name,similarity=similarity,exclusionScore=denied,excluded=denied>similarity,score=denied>similarity?0:similarity};})
                    .OrderByDescending(s=>s.score).ThenBy(s=>s.tag,StringComparer.Ordinal).ToList();
            var semanticFirst=result.semanticTags.Count>0&&assets.Any(a=>a.Semantics.Length>0);
            result.semanticMode=semanticFirst?"semantic_first":"emotion";
            var semanticScores=assets.Select(asset=>result.semanticTags
                .Where(s=>asset.Semantics.Contains(s.tag,StringComparer.Ordinal))
                .Select(s=>s.excluded?-Math.Max(0,s.exclusionScore-s.similarity):s.score).DefaultIfEmpty(0).Max()).ToArray();
            // Keep every tie in the leading semantic band. Emotion cannot promote
            // an unrelated image, and a uniformly weak signal still has candidates.
            var bestSemantic=semanticScores.Max();
            // Embeddings use floats; tolerate their rounding at the band boundary.
            var shortlisted=semanticScores.Select(score=>!semanticFirst||bestSemantic-score<=SemanticCandidateWindow+1e-6).ToArray();
            if(!string.IsNullOrWhiteSpace(query.mood)&&!string.IsNullOrWhiteSpace(query.text))
            {
                var mood=await encoder.EmbedAsync(query.mood,token);
                Cos(vector,mood);vector=vector.Select((v,i)=>0.7f*v+0.3f*mood[i]).ToArray();
            }
            result.tags=Profile(vector,anchors).OrderByDescending(t=>t.score).ThenBy(t=>t.tag,StringComparer.Ordinal).ToList();
            var selected=result.tags.Where(t=>t.score>0).Take(n).ToList();
            var q=tags.Select(t=>(float)(selected.FirstOrDefault(s=>s.tag==t.Name)?.score??0)).ToArray();
            for(var i=0;i<assets.Count;i++)
            {
                double emotionScore=0;
                if(shortlisted[i])
                {
                    var weights=profiles[i].Select(t=>(float)t.score).ToArray();
                    emotionScore=weights.Any(v=>v>0)&&q.Any(v=>v>0)?Math.Max(0,Cos(q,weights)):0;
                }
                result.candidates.Add(new(){id=assets[i].Id,name=assets[i].Name,score=emotionScore,emotionScore=emotionScore,
                    semanticScore=semanticScores[i],eligible=shortlisted[i],
                    reason=!shortlisted[i]?"semantic_not_shortlisted":semanticFirst?"shortlisted_emotion_score":"emotion_score"});
            }
            result.candidates=result.candidates.OrderByDescending(a=>a.eligible).ThenByDescending(a=>a.score).ThenBy(a=>a.id,StringComparer.Ordinal).ToList();
            foreach(var candidate in result.candidates.Where(c=>c.eligible))
            {
                token.ThrowIfCancellationRequested();
                try {ReadImage(candidate.id);}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {candidate.eligible=false;candidate.reason="image_unavailable";continue;}
                result.selectedId=candidate.id;result.status="matched";result.reason="highest_available_score";break;
            }
            if(result.selectedId==null)result.reason="images_unavailable";
            return result;
        }
        finally {scoring.Release();}
    }
}
