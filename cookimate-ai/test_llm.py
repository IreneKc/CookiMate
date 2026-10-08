# test_llm.py — throwaway sanity check, delete after
from llama_cpp import Llama

llm = Llama(
    model_path="./models/qwen2.5-1.5b-instruct-q4_k_m.gguf",
    n_ctx=2048,
    verbose=False,
)

out = llm.create_chat_completion(
    messages=[{"role": "user", "content": "Reply with exactly: hello world"}],
    max_tokens=20,
)
print(out["choices"][0]["message"]["content"])
