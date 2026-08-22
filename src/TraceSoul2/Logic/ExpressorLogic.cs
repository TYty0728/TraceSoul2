using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>外显：心智组织好之后，决定怎么开口。不调内部神经。</summary>
    public sealed class ExpressorLogic
    {
        private readonly ILlmClient llm;

        public ExpressorLogic(ILlmClient llm)
        {
            this.llm = llm ?? throw new ArgumentNullException("llm");
        }

        public async Task<BrainStructuredOutputData> ExpressAsync(
            TraceTurnContext turn,
            IEnumerable<TracePluginMetadataData> plugins,
            IEnumerable<TraceContributionDescriptorData> catalog,
            IEnumerable<TraceContextBlockData> contextBlocks,
            MindDecisionData mind,
            string memoryFlesh,
            bool waitOnly,
            string leaveResult,
            CancellationToken cancellationToken)
        {
            var needsReply = waitOnly || turn.RequiresExpression;
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildFoundationPrompt(turn, contextBlocks)),
                new DeepSeekMessageData("system", BuildTurnPrompt(
                    turn, plugins, contextBlocks, mind, memoryFlesh, waitOnly, leaveResult)),
                new DeepSeekMessageData("user", turn.Moment.Content)
            };
            var raw = await DeepSeekStructuredOutputLogic.CompletePlainAsync(
                llm,
                messages,
                text => ReplyCarriesMind(ParseSpoken(text), mind, needsReply),
                waitOnly ? CorePrompts.Expressor.MissingWait
                    : CorePrompts.Expressor.MissingSpeak,
                cancellationToken);
            var expressed = ParseSpoken(raw);
            ApplyMindAtmosphere(expressed, mind, turn, waitOnly);
            EnsureExplicitImageRequest(expressed, turn, catalog);
            var mapped = MapExpressor(expressed, catalog, needsReply, waitOnly ? new MindDecisionData() : mind);
            var imageCalls = (mapped.expressions ?? new List<BrainCapabilityCallData>())
                .Where(x => x != null && string.Equals(x.purpose, BodyOrganValues.Image, StringComparison.Ordinal))
                .ToList();
            if (imageCalls.Count > 0)
                turn.Services?.LogTiming(turn.TraceId, "TA的相机 外显图片路由", detail:
                    "calls=" + imageCalls.Count + "｜capability=" +
                    string.Join(",", imageCalls.Select(x => x.capability_id)));
            return mapped;
        }

        /// <summary>要开口时，至少得真的说出话来。长短冷热由他自己按这一拍判断。</summary>
        public static bool ReplyCarriesMind(ExpressorOutputData expressed, MindDecisionData mind, bool needsReply)
        {
            if (expressed == null) return false;
            if (!needsReply) return true;
            return !string.IsNullOrWhiteSpace(expressed.reply);
        }

        /// <summary>
        /// 开口应是人话。若模型仍吐 {"reply":"..."}，拆出来说的那句，别把 JSON 发给她。
        /// </summary>
        public static ExpressorOutputData ParseSpoken(string raw)
        {
            var text = UnwrapWholeFence((raw ?? string.Empty).Trim());
            ExpressorOutputData parsed;
            if (TryReadReplyJson(text, out parsed))
            {
                parsed.reply = StripProtocolSpeak(parsed.reply);
                return parsed;
            }
            return new ExpressorOutputData { reply = StripProtocolSpeak(text) };
        }

        /// <summary>开口不能把出站占位当成台词念出来。</summary>
        internal static string StripProtocolSpeak(string source)
        {
            var text = source ?? string.Empty;
            if (text.IndexOf('[') < 0) return text.Trim();
            text = Regex.Replace(text, @"\[QQ[^\]]*\]", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[CQ:[^\]]*\]", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        /// <summary>
        /// 氛围向的出图只听心智的固定短答。开口人话里即使残留旧字段也不算数。
        /// 出门等一下、心跳未在对话里说话、睡着，都不由心智自己按快门。
        /// </summary>
        internal static void ApplyMindAtmosphere(
            ExpressorOutputData expressed,
            MindDecisionData mind,
            TraceTurnContext turn,
            bool waitOnly)
        {
            if (expressed == null) return;
            mind = MindLogic.Normalize(mind);
            if (waitOnly) return;
            if (HasImageExpression(expressed)) return;
            if (!mind.WantsImage()) return;
            var heartbeatQuiet = turn != null &&
                                 string.Equals(turn.Wake, KernelWakeValues.Mind, StringComparison.Ordinal) &&
                                 !turn.RequiresExpression;
            if (heartbeatQuiet) return;
            if (mind.ImageValue() == MindAtmosphereValues.Selfie)
            {
                expressed.image_mode = "selfie";
                expressed.image = SceneFromMind(mind,
                    "在此刻自然生活场景中面向镜头拍下的一张自拍，神情与当前对话氛围一致");
            }
            else if (mind.ImageValue() == MindAtmosphereValues.Draw)
            {
                expressed.image_mode = "draw";
                expressed.image = SceneFromMind(mind, "根据当前气氛生成一张画");
            }
        }

        private static string SceneFromMind(MindDecisionData mind, string fallback)
        {
            var note = mind == null ? string.Empty : (mind.note ?? string.Empty).Trim();
            if (note.Length > 0) return Limit(note, 500);
            var inner = mind == null ? string.Empty : (mind.inner ?? string.Empty).Trim();
            if (inner.Length > 0) return Limit(inner, 500);
            return fallback;
        }

        private static string UnwrapWholeFence(string text)
        {
            if (text.Length < 6 || !text.StartsWith("```", StringComparison.Ordinal)) return text;
            var firstLine = text.IndexOf('\n');
            if (firstLine < 0) return text;
            var body = text.Substring(firstLine + 1);
            var close = body.LastIndexOf("```", StringComparison.Ordinal);
            if (close < 0) return text;
            return body.Substring(0, close).Trim();
        }

        private static bool TryReadReplyJson(string text, out ExpressorOutputData parsed)
        {
            parsed = null;
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length < 8 || trimmed[0] != '{') return false;
            if (trimmed.IndexOf("\"reply\"", StringComparison.OrdinalIgnoreCase) < 0) return false;
            try
            {
                parsed = TraceSoul2.Util.TraceJson.FromJson<ExpressorOutputData>(
                    DeepSeekStructuredOutputLogic.EscapeRawControlsInJsonStrings(trimmed));
                return parsed != null && parsed.reply != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 明确索要照片/画图时，外显不能只用文字或语音敷衍过去。模型正常填写图片字段时不干预；
        /// 只有它漏填且真正的相机/生图器可用时，才补成同一套结构化图片动作。
        /// </summary>
        internal static void EnsureExplicitImageRequest(
            ExpressorOutputData expressed,
            TraceTurnContext turn,
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            if (expressed == null || turn == null || turn.Moment == null) return;
            var text = (turn.Moment.Content ?? string.Empty).Trim();
            if (text.Length == 0) return;
            var asksForImage = Regex.IsMatch(text,
                    @"(?:发|给|来|拍|看看|想看|试试|画|生成|做).{0,12}(?:照片|自拍|图片|图)",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text,
                    @"(?:照片|自拍|图片|图).{0,12}(?:发|给|来|拍|看|试试|画|生成|做|没发)",
                    RegexOptions.IgnoreCase);
            if (!asksForImage) return;

            if (HasImageExpression(expressed))
            {
                turn.Services?.LogTiming(turn.TraceId, "TA的相机 明确请求已由外显填写", detail:
                    "mode=" + (expressed.image_mode ?? string.Empty) + "｜prompt=" +
                    Limit(expressed.image ?? string.Empty, 240));
                return;
            }
            var imageEffector = FindImageEffector(catalog);
            if (imageEffector == null)
            {
                turn.Services?.LogTiming(turn.TraceId, "TA的相机 明确请求无法路由", detail:
                    "可用目录中没有接收 prompt/url 的图片器官");
                return;
            }

            var drawing = Regex.IsMatch(text, @"(?:画|生成|做).{0,12}(?:图片|图)", RegexOptions.IgnoreCase) &&
                          text.IndexOf("照片", StringComparison.Ordinal) < 0 &&
                          text.IndexOf("自拍", StringComparison.Ordinal) < 0;
            expressed.image = drawing
                ? "根据当前请求生成画面：" + Limit(text, 500)
                : "在此刻自然生活场景中面向镜头拍下的一张自拍，神情与当前对话氛围一致";
            expressed.image_mode = drawing ? "draw" : "selfie";
            turn.Services?.LogTiming(turn.TraceId, "TA的相机 明确请求兜底补图", detail:
                "capability=" + imageEffector.Id + "｜mode=" + expressed.image_mode +
                "｜prompt=" + Limit(expressed.image, 240));
        }

        private static bool HasImageExpression(ExpressorOutputData expressed)
        {
            if (!string.IsNullOrWhiteSpace(expressed.image)) return true;
            return (expressed.images ?? new List<ExpressorImageOutputData>()).Any(x =>
                x != null && (!string.IsNullOrWhiteSpace(x.prompt) || !string.IsNullOrWhiteSpace(x.url)));
        }

        /// <summary>生图走后台：先把话说出去，图成功后再单独发。</summary>
        internal static bool IsImageExpression(BrainCapabilityCallData extra)
        {
            if (extra == null || string.IsNullOrWhiteSpace(extra.capability_id)) return false;
            if (string.Equals(extra.purpose, BodyOrganValues.Image, StringComparison.Ordinal)) return true;
            var id = extra.capability_id;
            return id.IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (id.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    id.IndexOf("send", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static void PartitionExpressions(
            IEnumerable<BrainCapabilityCallData> expressions,
            out List<BrainCapabilityCallData> immediate,
            out List<BrainCapabilityCallData> images)
        {
            immediate = new List<BrainCapabilityCallData>();
            images = new List<BrainCapabilityCallData>();
            foreach (var extra in expressions ?? Enumerable.Empty<BrainCapabilityCallData>())
            {
                if (extra == null || string.IsNullOrWhiteSpace(extra.capability_id)) continue;
                if (IsImageExpression(extra)) images.Add(extra);
                else immediate.Add(extra);
            }
        }

        public static BrainStructuredOutputData MapExpressor(
            ExpressorOutputData expressed,
            IEnumerable<TraceContributionDescriptorData> catalog,
            bool requiresExpression,
            MindDecisionData mind = null)
        {
            expressed = expressed ?? new ExpressorOutputData();
            var legacyVoices = new List<ExpressorVoiceOutputData>();
            var legacyImages = new List<ExpressorImageOutputData>();
            var cleanedReply = ParseLegacyMarkers(expressed.reply, legacyVoices, legacyImages);
            var effectors = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x != null && x.Kind == TraceContributionKindValues.Effector)
                .ToList();
            var output = new BrainStructuredOutputData
            {
                state = BrainStepStateValues.Finish,
                mode = BrainModeValues.Reflex,
                intent = string.Empty,
                decision_summary = string.Empty,
                calls = new List<BrainCapabilityCallData>(),
                should_express = requiresExpression || !string.IsNullOrWhiteSpace(cleanedReply) ||
                                 legacyVoices.Count > 0 || legacyImages.Count > 0,
                reply = cleanedReply,
                expressions = new List<BrainCapabilityCallData>(),
                facet_outputs = new List<BrainFacetOutputData>()
            };
            mind = MindLogic.Normalize(mind);
            var sticker = mind.WantsSticker() ? (mind.mood ?? string.Empty).Trim() : string.Empty;
            AddExtra(output.expressions, effectors, BodyOrganValues.Sticker, "emotion", sticker);
            AddExtra(output.expressions, effectors, BodyOrganValues.Qzone, "content", expressed.qzone);
            AddVoice(output.expressions, effectors, expressed.voice, expressed.voice_emotion);
            foreach (var voice in expressed.voices ?? new List<ExpressorVoiceOutputData>())
                if (voice != null) AddVoice(output.expressions, effectors, voice.text, voice.emotion);
            foreach (var voice in legacyVoices)
                AddVoice(output.expressions, effectors, voice.text, voice.emotion);
            AddImage(output.expressions, effectors, expressed.image, expressed.image_mode,
                expressed.image_refs, expressed.image_aspect_ratio, null);
            foreach (var image in expressed.images ?? new List<ExpressorImageOutputData>())
                if (image != null) AddImage(output.expressions, effectors, image.prompt, image.mode,
                    image.refs, image.aspect_ratio, image.url);
            foreach (var image in legacyImages)
                AddImage(output.expressions, effectors, image.prompt, image.mode,
                    image.refs, image.aspect_ratio, image.url);
            return output;
        }

        /// <summary>
        /// 兼容老 AstrBot 插件留下的输出习惯。新结构使用 voice/images 字段，旧标签只作为迁移兜底，
        /// 命中后会从可见文字中剥离并转换为同一套器官调用，避免把控制标记裸发给用户。
        /// </summary>
        private static string ParseLegacyMarkers(
            string source,
            List<ExpressorVoiceOutputData> voices,
            List<ExpressorImageOutputData> images)
        {
            var text = source ?? string.Empty;
            text = Regex.Replace(text,
                @"<\s*voice(?:\s+emotion\s*=\s*[\""'](?<emotion>[^\""']+)[\""'])?\s*>(?<text>[\s\S]*?)<\s*/\s*voice\s*>",
                match =>
                {
                    var content = match.Groups["text"].Value.Trim();
                    if (content.Length > 0) voices.Add(new ExpressorVoiceOutputData
                    {
                        text = content,
                        emotion = match.Groups["emotion"].Success ? match.Groups["emotion"].Value.Trim() : string.Empty
                    });
                    return "\n";
                }, RegexOptions.IgnoreCase);

            text = Regex.Replace(text,
                @"\[\s*(?<kind>photo|selfie|draw|edit)\s*:(?<prompt>(?:[^\[\]]*|\[[^\]]*\])*)\s*\]",
                match =>
                {
                    var kind = match.Groups["kind"].Value.ToLowerInvariant();
                    var prompt = match.Groups["prompt"].Value.Trim();
                    var refs = new List<string>();
                    foreach (Match refMatch in Regex.Matches(prompt, @"\[\s*ref\s*:\s*(?<name>.*?)\s*\]",
                                 RegexOptions.IgnoreCase))
                    {
                        var name = refMatch.Groups["name"].Value.Trim();
                        if (name.Length > 0) refs.Add(name);
                    }
                    var useUserRef = Regex.IsMatch(prompt, @"\[\s*use_user_ref\s*\]", RegexOptions.IgnoreCase);
                    prompt = Regex.Replace(prompt, @"\[\s*ref\s*:\s*.*?\s*\]", string.Empty,
                        RegexOptions.IgnoreCase);
                    prompt = Regex.Replace(prompt, @"\[\s*use_user_ref\s*\]", string.Empty,
                        RegexOptions.IgnoreCase).Trim();
                    if (useUserRef) refs.Insert(0, "当前消息");
                    if (prompt.Length > 0) images.Add(new ExpressorImageOutputData
                    {
                        prompt = prompt,
                        mode = kind == "photo" || kind == "selfie" ? "selfie" : kind,
                        refs = string.Join(",", refs.Distinct(StringComparer.OrdinalIgnoreCase))
                    });
                    return "\n";
                }, RegexOptions.IgnoreCase);

            text = Regex.Replace(text, @"\[\s*img\s*:\s*(?<url>https?://[^\s\]]+)\s*\]",
                match =>
                {
                    images.Add(new ExpressorImageOutputData
                    {
                        prompt = "发送指定图片",
                        mode = "url",
                        url = match.Groups["url"].Value.Trim()
                    });
                    return "\n";
                }, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        private static void AddVoice(
            List<BrainCapabilityCallData> expressions,
            List<TraceContributionDescriptorData> effectors,
            string text,
            string emotion)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            var match = FindEffector(effectors, BodyOrganValues.Voice);
            if (match == null) return;
            var arguments = new List<BrainCallArgumentData>
            {
                new BrainCallArgumentData { name = "text", value = Limit(text, 4000) }
            };
            if (!string.IsNullOrWhiteSpace(emotion))
                arguments.Add(new BrainCallArgumentData { name = "emotion", value = Limit(emotion.Trim(), 40) });
            expressions.Add(NewExpression(match.Id, BodyOrganValues.Voice, arguments));
        }

        private static void AddImage(
            List<BrainCapabilityCallData> expressions,
            List<TraceContributionDescriptorData> effectors,
            string prompt,
            string mode,
            string refs,
            string aspectRatio,
            string url)
        {
            prompt = (prompt ?? string.Empty).Trim();
            url = (url ?? string.Empty).Trim();
            if (prompt.Length == 0 && url.Length == 0) return;
            var match = FindImageEffector(effectors);
            if (match == null) return;
            var arguments = new List<BrainCallArgumentData>();
            if (prompt.Length > 0)
                arguments.Add(new BrainCallArgumentData { name = "prompt", value = Limit(prompt, 4000) });
            if (!string.IsNullOrWhiteSpace(mode))
                arguments.Add(new BrainCallArgumentData { name = "mode", value = Limit(mode.Trim(), 20) });
            if (!string.IsNullOrWhiteSpace(refs))
                arguments.Add(new BrainCallArgumentData { name = "refs", value = Limit(refs.Trim(), 500) });
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                arguments.Add(new BrainCallArgumentData { name = "aspect_ratio", value = Limit(aspectRatio.Trim(), 20) });
            if (url.Length > 0)
                arguments.Add(new BrainCallArgumentData { name = "url", value = Limit(url, 3000) });
            expressions.Add(NewExpression(match.Id, BodyOrganValues.Image, arguments));
        }

        private static TraceContributionDescriptorData FindEffector(
            IEnumerable<TraceContributionDescriptorData> effectors,
            string organ)
        {
            return (effectors ?? Enumerable.Empty<TraceContributionDescriptorData>()).FirstOrDefault(x =>
                string.Equals(MouthLogic.OrganOf(x), organ, StringComparison.Ordinal) ||
                (x.Id ?? string.Empty).IndexOf(organ, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Provides ?? string.Empty).IndexOf(organ, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 图片器官里既有“把已经存在的 file 发到 QQ”的底层直发器，也有真正接收 prompt/url
        /// 的相机/生图器。外显产出的结构是后者，不能因为程序集加载顺序让 qq.image.send 抢走。
        /// </summary>
        private static TraceContributionDescriptorData FindImageEffector(
            IEnumerable<TraceContributionDescriptorData> effectors)
        {
            return (effectors ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x != null &&
                            (string.Equals(MouthLogic.OrganOf(x), BodyOrganValues.Image, StringComparison.Ordinal) ||
                             (x.Id ?? string.Empty).IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (x.Provides ?? string.Empty).IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0))
                .Where(CanAcceptExpressorImage)
                .OrderByDescending(ImageEffectorScore)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static bool CanAcceptExpressorImage(TraceContributionDescriptorData descriptor)
        {
            var schema = descriptor.ParametersJsonSchema ?? string.Empty;
            return schema.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   schema.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (descriptor.Id ?? string.Empty).IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (descriptor.Provides ?? string.Empty).IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ImageEffectorScore(TraceContributionDescriptorData descriptor)
        {
            var score = 0;
            var id = descriptor.Id ?? string.Empty;
            var provides = descriptor.Provides ?? string.Empty;
            var schema = descriptor.ParametersJsonSchema ?? string.Empty;
            if (id.IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
            if (provides.IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
            if (schema.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
            if (schema.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
            return score;
        }

        private static BrainCapabilityCallData NewExpression(
            string capabilityId,
            string purpose,
            List<BrainCallArgumentData> arguments)
        {
            return new BrainCapabilityCallData
            {
                call_id = "expr-" + purpose + "-" + Guid.NewGuid().ToString("N"),
                capability_id = capabilityId,
                purpose = purpose,
                arguments = arguments ?? new List<BrainCallArgumentData>()
            };
        }

        private static void AddExtra(
            List<BrainCapabilityCallData> expressions,
            List<TraceContributionDescriptorData> effectors,
            string needle,
            string argumentName,
            string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return;
            var match = effectors.FirstOrDefault(x =>
                string.Equals(MouthLogic.OrganOf(x), needle, StringComparison.Ordinal) ||
                (x.Id ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Provides ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null) return;
            expressions.Add(new BrainCapabilityCallData
            {
                call_id = "expr-" + needle,
                capability_id = match.Id,
                purpose = needle,
                arguments = new List<BrainCallArgumentData>
                {
                    new BrainCallArgumentData { name = argumentName, value = Limit(value, 3000) }
                }
            });
        }

        public static BrainStructuredOutputData NormalizeStep(
            BrainStructuredOutputData output,
            IEnumerable<TraceContributionDescriptorData> catalog,
            bool mustFinish,
            bool requiresExpression,
            string defaultExpressionCapabilityId = null)
        {
            if (output == null) throw new ArgumentNullException("output");
            var descriptors = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>()).ToList();
            var callable = new HashSet<string>(descriptors
                .Where(x => x.Kind == TraceContributionKindValues.CallableNerve).Select(x => x.Id));
            var effectors = descriptors.Where(x => x.Kind == TraceContributionKindValues.Effector).ToList();
            var writableFacets = new HashSet<string>(descriptors
                .Where(x => x.Kind == TraceContributionKindValues.MountedFacet &&
                            !string.IsNullOrWhiteSpace(x.OutputJsonSchema)).Select(x => x.Id));

            output.state = (output.state ?? string.Empty).Trim().ToLowerInvariant();
            output.mode = (output.mode ?? string.Empty).Trim().ToLowerInvariant();
            if (!BrainModeValues.IsKnown(output.mode)) output.mode = BrainModeValues.Focused;
            output.intent = Limit((output.intent ?? string.Empty).Trim(), 160);
            output.decision_summary = Limit((output.decision_summary ?? string.Empty).Trim(), 240);
            output.reply = (output.reply ?? string.Empty).Trim();
            output.expression_capability_id = (output.expression_capability_id ?? string.Empty).Trim();
            output.calls = NormalizeCalls(output.calls, callable);

            if (mustFinish) output.state = BrainStepStateValues.Finish;
            if (output.state == BrainStepStateValues.Call && output.calls.Count > 0)
            {
                if (output.mode == BrainModeValues.Reflex) output.mode = BrainModeValues.Focused;
                output.reply = string.Empty;
                output.expression_capability_id = string.Empty;
                output.expressions = new List<BrainCapabilityCallData>();
                output.facet_outputs = new List<BrainFacetOutputData>();
                return output;
            }

            output.state = BrainStepStateValues.Finish;
            output.calls = new List<BrainCapabilityCallData>();
            output.facet_outputs = NormalizeFacetOutputs(output.facet_outputs, writableFacets);
            if (requiresExpression) output.should_express = true;
            if (!output.should_express)
            {
                output.expression_capability_id = string.Empty;
                output.reply = string.Empty;
                output.expressions = new List<BrainCapabilityCallData>();
                return output;
            }
            if (effectors.Any(x => x.Id == defaultExpressionCapabilityId))
                output.expression_capability_id = defaultExpressionCapabilityId;
            else if (!effectors.Any(x => x.Id == output.expression_capability_id))
            {
                if (effectors.Count == 1) output.expression_capability_id = effectors[0].Id;
                else throw new InvalidOperationException("外显没有选择当前可用的外部表达器。");
            }
            if (output.reply.Length == 0)
                throw new InvalidOperationException("外显完成时没有给出表达内容。");
            // 附加表达：开放列表（表情/图片/语音/动作……），只保留有效的 effector，数量由外显自然决定。
            output.expressions = NormalizeExpressions(output.expressions, effectors);
            return output;
        }

        /// <summary>附加表达的规范化：逐个校验必须是 effector，参数清洗，不设数量上限。</summary>
        private static List<BrainCapabilityCallData> NormalizeExpressions(
            IEnumerable<BrainCapabilityCallData> source,
            List<TraceContributionDescriptorData> effectors)
        {
            var ids = new HashSet<string>(effectors.Select(x => x.Id));
            var result = new List<BrainCapabilityCallData>();
            foreach (var item in source ?? Enumerable.Empty<BrainCapabilityCallData>())
            {
                if (item == null || !ids.Contains(item.capability_id ?? string.Empty)) continue;
                item.call_id = string.IsNullOrWhiteSpace(item.call_id)
                    ? Guid.NewGuid().ToString("N") : Limit(item.call_id.Trim(), 80);
                item.purpose = Limit((item.purpose ?? string.Empty).Trim(), 160);
                item.arguments = (item.arguments ?? new List<BrainCallArgumentData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainCallArgumentData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 3000)
                    }).ToList();
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// 外显可缓存前缀：完整短卡（含表达习惯）、持续状态、一张嘴、开口格式。
        /// 不含工具目录。心智决策与记忆血肉放在动态段。
        /// </summary>
        private static string BuildFoundationPrompt(
            TraceTurnContext turn,
            IEnumerable<TraceContextBlockData> contextBlocks)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var blocks = (contextBlocks ?? Enumerable.Empty<TraceContextBlockData>())
                .Where(x => x != null && !IsRedundantProtocolFacet(x.FacetId) &&
                            !IsTurnDynamicFacet(x.FacetId))
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.FacetId, StringComparer.Ordinal)
                .ToList();

            var builder = new StringBuilder();
            var identity = blocks.FirstOrDefault(x =>
                string.Equals(x.FacetId, "identity.base", StringComparison.Ordinal));
            if (identity != null && !string.IsNullOrWhiteSpace(identity.Content))
                builder.AppendLine(identity.Content.Trim());
            else
                builder.AppendLine(pair.IsComplete ? "我是" + pair.Assname + "。" : CorePrompts.Expressor.SelfFallback);
            builder.AppendLine();

            var continuing = blocks.Where(x => !ReferenceEquals(x, identity)).ToList();
            if (continuing.Count > 0)
            {
                builder.AppendLine(CorePrompts.Expressor.ContinuingHeader);
                builder.AppendLine(CorePrompts.Expressor.ContinuingHint);
                foreach (var block in continuing)
                {
                    builder.AppendLine(block.Content);
                    builder.AppendLine();
                }
            }

            builder.AppendLine();
            AppendOutputFormat(builder);
            return builder.ToString();
        }

        /// <summary>只放当前轮变化内容：时间、心智组织卡、记忆血肉、此刻。</summary>
        private static string BuildTurnPrompt(
            TraceTurnContext turn,
            IEnumerable<TracePluginMetadataData> plugins,
            IEnumerable<TraceContextBlockData> contextBlocks,
            MindDecisionData mind,
            string memoryFlesh,
            bool waitOnly,
            string leaveResult)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var builder = new StringBuilder();
            var time = (contextBlocks ?? Enumerable.Empty<TraceContextBlockData>())
                .FirstOrDefault(x => x != null &&
                                     string.Equals(x.FacetId, "time.context", StringComparison.Ordinal));
            if (time != null && !string.IsNullOrWhiteSpace(time.Content))
            {
                builder.AppendLine(time.Content.Trim());
                builder.AppendLine();
            }
            var recentDialogue = MindLogic.FormatRecentDialogue(turn);
            if (!string.IsNullOrWhiteSpace(recentDialogue))
            {
                builder.AppendLine(recentDialogue);
                builder.AppendLine();
            }
            builder.AppendLine(CorePrompts.Expressor.ThoughtHeader);
            builder.AppendLine(FormatMind(mind));
            if (!string.IsNullOrWhiteSpace(memoryFlesh))
            {
                builder.AppendLine();
                builder.AppendLine(memoryFlesh.Trim());
                builder.AppendLine(pair.Apply(CorePrompts.Expressor.MemoryFlesh));
            }
            if (!string.IsNullOrWhiteSpace(leaveResult))
            {
                builder.AppendLine();
                builder.AppendLine(CorePrompts.Expressor.LeaveResultHeader);
                builder.AppendLine(leaveResult.Trim());
            }
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Expressor.NowHeader);
            if (waitOnly)
            {
                builder.AppendLine(CorePrompts.Expressor.LeaveWait);
            }
            else if (turn.RequiresExpression)
            {
                builder.AppendLine(pair.Apply(CorePrompts.Expressor.PrivateChat));
            }
            else if (mind != null && mind.speak)
            {
                builder.AppendLine(CorePrompts.Expressor.Proactive);
            }
            else
            {
                builder.AppendLine(CorePrompts.Expressor.Silent);
            }
            builder.AppendLine(CorePrompts.Expressor.SpeakPlain);
            return builder.ToString();
        }

        private static string FormatMind(MindDecisionData mind)
        {
            mind = MindLogic.Normalize(mind);
            var builder = new StringBuilder();
            if (mind.ParseTags().Count > 0)
                builder.AppendLine(CorePrompts.Expressor.MindTagsPrefix + string.Join("、", mind.ParseTags()));
            if (!string.IsNullOrWhiteSpace(mind.mood))
                builder.AppendLine(CorePrompts.Expressor.MindMoodPrefix + mind.mood + (mind.mood_changed ? CorePrompts.Expressor.MindMoodChanged : string.Empty));
            if (!string.IsNullOrWhiteSpace(mind.inner))
                builder.AppendLine(CorePrompts.Expressor.MindInnerPrefix + mind.inner);
            if (mind.archive) builder.AppendLine(CorePrompts.Expressor.MindArchive);
            if (!string.IsNullOrWhiteSpace(mind.new_fact))
                builder.AppendLine(CorePrompts.Expressor.MindNewFactPrefix + mind.new_fact);
            if (!string.IsNullOrWhiteSpace(mind.leave))
                builder.AppendLine(CorePrompts.Expressor.MindLeavePrefix + mind.leave);
            if (!string.IsNullOrWhiteSpace(mind.note))
                builder.AppendLine(CorePrompts.Expressor.MindNotePrefix + mind.note);
            if (!string.IsNullOrWhiteSpace(mind.cognition))
                builder.AppendLine(CorePrompts.Expressor.MindCognitionPrefix + mind.cognition);
            if (mind.WantsSticker())
                builder.AppendLine(CorePrompts.Expressor.MindSticker);
            if (mind.ImageValue() == MindAtmosphereValues.Selfie)
                builder.AppendLine(CorePrompts.Expressor.MindSelfie);
            else if (mind.ImageValue() == MindAtmosphereValues.Draw)
                builder.AppendLine(CorePrompts.Expressor.MindDraw);
            return builder.ToString().TrimEnd();
        }

        private static void AppendOutputFormat(StringBuilder builder)
        {
            CorePrompts.Write(builder, CorePrompts.Expressor.OutputFormat);
        }

        private static bool IsRedundantProtocolFacet(string facetId)
        {
            if (string.IsNullOrWhiteSpace(facetId)) return true;
            if (string.Equals(facetId, "qq.reply.channel", StringComparison.Ordinal)) return true;
            if (string.Equals(facetId, "senses.catalog", StringComparison.Ordinal)) return true;
            return facetId.EndsWith(".usage", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTurnDynamicFacet(string facetId)
        {
            return string.Equals(facetId, "time.context", StringComparison.Ordinal) ||
                   string.Equals(facetId, "inner.snapshot", StringComparison.Ordinal) ||
                   string.Equals(facetId, "memory.today.new", StringComparison.Ordinal) ||
                   string.Equals(facetId, "day.trajectory", StringComparison.Ordinal) ||
                   string.Equals(facetId, "memory.ladder.snapshot", StringComparison.Ordinal);
        }

        private static string FriendlySource(
            string sourceId,
            IEnumerable<TracePluginMetadataData> plugins)
        {
            var plugin = (plugins ?? Enumerable.Empty<TracePluginMetadataData>())
                .FirstOrDefault(x => string.Equals(x.Id, sourceId, StringComparison.Ordinal));
            if (plugin != null && !string.IsNullOrWhiteSpace(plugin.DisplayName)) return plugin.DisplayName;
            if (sourceId == "builtin.dialogue") return "本地文字对话";
            if (sourceId == "builtin.onebot") return "QQ 对话";
            if (sourceId == "builtin.time") return "时间事件";
            return "外部感知";
        }

        private static string FriendlyRealm(string value)
        {
            if (value == TraceRealmValues.ExternalWorld) return "现实世界";
            if (value == TraceRealmValues.SharedScene) return "共同情境";
            if (value == TraceRealmValues.Meta) return "关于系统本身";
            if (value == TraceRealmValues.ExplicitFiction) return "明确虚构";
            return "尚未分类";
        }

        private static string FriendlyEvidence(string value)
        {
            if (value == EvidenceTypeValues.UserReported) return "对方自述";
            if (value == EvidenceTypeValues.PluginObserved) return "感官观察";
            if (value == EvidenceTypeValues.SharedSceneDeclared) return "共同情境中的明确表达";
            if (value == EvidenceTypeValues.AssPerformed) return "我已实际做出";
            if (value == EvidenceTypeValues.ExplicitFiction) return "明确虚构内容";
            if (value == EvidenceTypeValues.DialogueExplicit) return "当前对话原话";
            return "来源未标明";
        }

        private static string FriendlyStatus(string value)
        {
            if (string.Equals(value, "success", StringComparison.OrdinalIgnoreCase)) return "成功";
            if (string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)) return "失败";
            if (string.Equals(value, "empty", StringComparison.OrdinalIgnoreCase)) return "没有内容";
            if (string.Equals(value, "no_memory", StringComparison.OrdinalIgnoreCase)) return "没有匹配记忆";
            return string.IsNullOrWhiteSpace(value) ? "已返回" : value;
        }

        public static bool IsDailyReview(MomentRecord moment)
        {
            return KernelWakeLogic.LooksLikeDailyReview(moment == null ? string.Empty : moment.Content);
        }

        private static List<BrainCapabilityCallData> NormalizeCalls(
            IEnumerable<BrainCapabilityCallData> source,
            HashSet<string> callable)
        {
            var result = new List<BrainCapabilityCallData>();
            foreach (var call in source ?? Enumerable.Empty<BrainCapabilityCallData>())
            {
                if (call == null || !callable.Contains(call.capability_id ?? string.Empty)) continue;
                call.call_id = string.IsNullOrWhiteSpace(call.call_id)
                    ? Guid.NewGuid().ToString("N") : Limit(call.call_id.Trim(), 80);
                call.purpose = Limit((call.purpose ?? string.Empty).Trim(), 160);
                call.arguments = (call.arguments ?? new List<BrainCallArgumentData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainCallArgumentData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 3000)
                    }).ToList();
                if (result.Any(x => x.call_id == call.call_id)) continue;
                result.Add(call);
                if (result.Count == 4) break;
            }
            return result;
        }

        private static List<BrainFacetOutputData> NormalizeFacetOutputs(
            IEnumerable<BrainFacetOutputData> source,
            HashSet<string> writable)
        {
            var result = new List<BrainFacetOutputData>();
            foreach (var item in source ?? Enumerable.Empty<BrainFacetOutputData>())
            {
                if (item == null || !writable.Contains(item.facet_id ?? string.Empty)) continue;
                item.summary = Limit((item.summary ?? string.Empty).Trim(), 240);
                item.fields = (item.fields ?? new List<BrainFacetFieldData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainFacetFieldData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 1000)
                    }).ToList();
                if (result.Any(x => x.facet_id == item.facet_id)) continue;
                result.Add(item);
            }
            return result;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
