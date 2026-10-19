"""CUDA replay for the pinned Qwen 16-codebook predictor. Original weights/sampling."""
from types import SimpleNamespace


class PredictorGraph:
    def __init__(self, predictor):
        self.predictor = predictor
        self.original = predictor.generate
        self.state = None

    def run(self, embeds, steps, sample, temperature, top_k, top_p):
        import torch
        from transformers.generation.logits_process import TemperatureLogitsWarper, TopKLogitsWarper, TopPLogitsWarper
        processors = []
        if sample:
            if temperature != 1:
                processors.append(TemperatureLogitsWarper(temperature))
            if top_k:
                processors.append(TopKLogitsWarper(top_k))
            if top_p < 1:
                processors.append(TopPLogitsWarper(top_p))
        cache, previous, tokens = None, None, []
        for step in range(steps):
            position = torch.arange(0 if step == 0 else step+1, step+2, device=embeds.device)
            output = self.predictor.forward(
                inputs_embeds=embeds if step == 0 else None,
                input_ids=previous, past_key_values=cache, generation_steps=step,
                attention_mask={'full_attention':None}, cache_position=position,
                position_ids=position.unsqueeze(0), use_cache=True, output_hidden_states=False)
            cache = output.past_key_values
            logits = output.logits[:, -1, :].float().clone()
            for processor in processors:
                logits = processor(None, logits)
            previous = torch.multinomial(torch.softmax(logits, dim=-1), 1) if sample else logits.argmax(dim=-1, keepdim=True)
            tokens.append(previous)
        return torch.cat(tokens, dim=-1)

    def __call__(self, *, inputs_embeds, max_new_tokens, do_sample, top_p, top_k, temperature, **kwargs):
        import torch
        key = (tuple(inputs_embeds.shape), inputs_embeds.dtype, inputs_embeds.device,
               max_new_tokens, do_sample, temperature, top_k, top_p)
        if (not torch.is_inference_mode_enabled() or inputs_embeds.device.type != 'cuda' or
                inputs_embeds.shape[:2] != (1, 2) or max_new_tokens != 15 or
                set(kwargs) - {'output_hidden_states','return_dict_in_generate'}):
            return self.original(inputs_embeds=inputs_embeds,max_new_tokens=max_new_tokens,do_sample=do_sample,
                                 top_p=top_p,top_k=top_k,temperature=temperature,**kwargs)
        if self.state is None:
            static = inputs_embeds.detach().clone()
            current = torch.cuda.current_stream(inputs_embeds.device)
            stream = torch.cuda.Stream(device=inputs_embeds.device)
            stream.wait_stream(current)
            with torch.random.fork_rng(devices=[inputs_embeds.device.index]):
                with torch.cuda.stream(stream):
                    for _ in range(3):
                        self.run(static,max_new_tokens,do_sample,temperature,top_k,top_p)
                current.wait_stream(stream)
                graph = torch.cuda.CUDAGraph()
                with torch.cuda.graph(graph,stream=stream):
                    result = self.run(static,max_new_tokens,do_sample,temperature,top_k,top_p)
                current.wait_stream(stream)
            self.state = (key,static,result,graph)
        elif self.state[0] != key:
            return self.original(inputs_embeds=inputs_embeds,max_new_tokens=max_new_tokens,do_sample=do_sample,
                                 top_p=top_p,top_k=top_k,temperature=temperature,**kwargs)
        _,static,result,graph = self.state
        static.copy_(inputs_embeds)
        graph.replay()
        return SimpleNamespace(sequences=result.clone())


def enable_predictor_graph(model):
    """Specialize just the known full-attention predictor; other shapes/settings use HF."""
    import torch
    predictor = model.model.talker.code_predictor
    if (predictor.__class__.__name__ != 'Qwen3TTSTalkerCodePredictorModelForConditionalGeneration' or
            model.model.talker.config.num_code_groups != 16 or predictor.model.has_sliding_layers):
        return 'standard'
    runner = PredictorGraph(predictor)
    embedding = model.model.talker.get_input_embeddings().weight
    # Warm capture before accepting requests. No reference audio, user text, or retained RNG change.
    with torch.inference_mode(), torch.random.fork_rng(devices=[embedding.device.index]):
        runner(inputs_embeds=torch.zeros(1,2,embedding.shape[1],device=embedding.device,dtype=embedding.dtype),
               max_new_tokens=15,do_sample=True,temperature=0.8,top_k=50,top_p=0.95,
               output_hidden_states=True,return_dict_in_generate=True)
        torch.cuda.synchronize()
    predictor.generate = runner
    return 'predictor_cuda_graph'
