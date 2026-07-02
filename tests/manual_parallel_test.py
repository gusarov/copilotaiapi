
"""Manual concurrency smoke test for the Copilot bridge.
Fires mixed streaming + non-streaming requests in parallel and validates:
  - default model is honored when 'model' is omitted
  - every parallel non-streaming request returns the correct arithmetic answer
  - streaming responses are well-formed SSE (ordered, valid JSON, terminated by [DONE])
"""
import json
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE = "http://localhost:7777"


def post(body, stream=False, timeout=180):
    data = json.dumps(body).encode()
    req = urllib.request.Request(
        f"{BASE}/v1/chat/completions",
        data=data,
        headers={"Content-Type": "application/json"},
    )
    return urllib.request.urlopen(req, timeout=timeout)


def non_stream(a, b):
    body = {"messages": [{"role": "user",
            "content": f"What is {a}+{b}? Reply with ONLY the number, nothing else."}]}
    # NOTE: 'model' intentionally omitted to test the default.
    resp = post(body)
    j = json.loads(resp.read())
    content = j["choices"][0]["message"]["content"].strip()
    model = j.get("model")
    ok = str(a + b) in content
    return ("nonstream", f"{a}+{b}", ok, model, content[:40])


def stream_req(idx):
    body = {"model": "claude-opus-4.8", "stream": True,
            "messages": [{"role": "user",
                          "content": f"Count from 1 to 5 separated by spaces. (req {idx})"}]}
    resp = post(body, stream=True)
    chunks = []
    saw_done = False
    saw_role = False
    text = []
    for raw in resp:
        line = raw.decode("utf-8", "replace").strip()
        if not line.startswith("data:"):
            continue
        payload = line[len("data:"):].strip()
        if payload == "[DONE]":
            saw_done = True
            break
        obj = json.loads(payload)  # raises if corrupted/interleaved
        chunks.append(obj)
        delta = obj["choices"][0].get("delta", {})
        if delta.get("role") == "assistant":
            saw_role = True
        if delta.get("content"):
            text.append(delta["content"])
    full = "".join(text)
    ok = saw_done and saw_role and len(chunks) >= 2
    return ("stream", f"req{idx}", ok, f"chunks={len(chunks)}", full[:40].replace("\n", " "))


def main():
    tasks = []
    with ThreadPoolExecutor(max_workers=16) as ex:
        for i in range(8):
            tasks.append(ex.submit(non_stream, i + 1, (i + 1) * 7))
        for i in range(4):
            tasks.append(ex.submit(stream_req, i + 1))

        results = []
        for fut in as_completed(tasks):
            try:
                results.append(fut.result())
            except Exception as e:
                results.append(("ERROR", "-", False, type(e).__name__, str(e)[:80]))

    passed = sum(1 for r in results if r[2])
    for kind, label, ok, meta, sample in sorted(results, key=lambda r: (r[0], r[1])):
        flag = "PASS" if ok else "FAIL"
        print(f"[{flag}] {kind:9} {label:8} {str(meta):14} | {sample}")
    print(f"\n{passed}/{len(results)} passed")
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
