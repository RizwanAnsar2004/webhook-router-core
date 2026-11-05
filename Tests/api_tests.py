#!/usr/bin/env python3
"""End-to-end tests for the webhook router.

Starts mock downstream services, launches the router with test configuration
(unless --base points at an already running instance), sends encrypted HyperPay
webhooks and checks where each one is delivered.
Exit code = number of failed checks.
"""

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import requests
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "WebhookRouter"
TEST_SECRET = os.environ.get("WEBHOOK_TEST_SECRET", "0f1e2d3c4b5a69788796a5b4c3d2e1f00112233445566778899aabbccddeeff0")
FAST_ACK_SECONDS = 0.5
DELIVERY_WAIT_SECONDS = 15
NO_DELIVERY_WAIT_SECONDS = 4
RETRY_GAP_LIMIT_SECONDS = 6

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
USE_COLOR = sys.stdout.isatty()


def paint(text, code):
    return f"\033[{code}m{text}\033[0m" if USE_COLOR else text


# ---------------------------------------------------------------- mock targets

class MockState:
    def __init__(self):
        self.lock = threading.Lock()
        self.received = []
        self.fail_remaining = {}

    def record(self, path, body, headers):
        with self.lock:
            self.received.append({"path": path, "body": body, "headers": headers, "at": time.time()})
            remaining = self.fail_remaining.get(path, 0)
            if remaining > 0:
                self.fail_remaining[path] = remaining - 1
                return 500
            return 200

    def deliveries(self, body):
        with self.lock:
            return [r for r in self.received if r["body"] == body]


STATE = MockState()


class MockHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        status = STATE.record(self.path, body, {k.lower(): v for k, v in self.headers.items()})
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"ok":true}' if status == 200 else b'{"ok":false}')

    def log_message(self, *args):
        pass


def start_mock(port):
    server = ThreadingHTTPServer(("127.0.0.1", port), MockHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


# ---------------------------------------------------------------- helpers

def encrypt(payload, secret=TEST_SECRET):
    iv = os.urandom(12)
    plain = payload if isinstance(payload, bytes) else json.dumps(payload).encode()
    sealed = AESGCM(bytes.fromhex(secret)).encrypt(iv, plain, None)
    cipher, tag = sealed[:-16], sealed[-16:]
    return cipher.hex().upper(), iv.hex().upper(), tag.hex().upper()


def hyperpay_event(identifier=None, include_custom=True):
    payload = {
        "id": uuid.uuid4().hex,
        "paymentType": "DB",
        "merchantTransactionId": f"T{uuid.uuid4().hex[:12]}",
        "result": {"code": "000.100.110", "description": "Request successfully processed"},
    }
    if include_custom:
        custom = {"3DS2_enrolled": "true"}
        if identifier is not None:
            custom["SHOPPER_service"] = identifier
        payload["customParameters"] = custom
    return {"type": "PAYMENT", "action": "CREATED", "payload": payload}


def wait_for_delivery(body, timeout=DELIVERY_WAIT_SECONDS):
    deadline = time.time() + timeout
    while time.time() < deadline:
        found = STATE.deliveries(body)
        if found:
            return found
        time.sleep(0.1)
    return []


# ---------------------------------------------------------------- runner

class Runner:
    def __init__(self, base):
        self.base = base.rstrip("/")
        self.passed = self.failed = self.skipped = 0

    def check(self, name, ok, payload=None, expected=None, actual=None):
        if ok:
            self.passed += 1
            print(f"{paint('[PASS]', '32')} {name}")
            return
        self.failed += 1
        print(f"{paint('[FAIL]', '31')} {name}")
        if payload is not None:
            print(f"       payload : {payload}")
        print(f"       expected: {expected}")
        print(f"       actual  : {actual}")

    def skip(self, name, reason):
        self.skipped += 1
        print(f"{paint('[SKIP]', '33')} {name} - {reason}")

    def post_webhook(self, body, iv, tag, content_type="text/plain", lowercase_headers=False):
        iv_name, tag_name = "X-Initialization-Vector", "X-Authentication-Tag"
        if lowercase_headers:
            iv_name, tag_name = iv_name.lower(), tag_name.lower()
        headers = {"Content-Type": content_type}
        if iv is not None:
            headers[iv_name] = iv
        if tag is not None:
            headers[tag_name] = tag
        started = time.time()
        response = requests.post(f"{self.base}/v1/api/webhooks/hyperpay", data=body.encode(), headers=headers, timeout=10)
        return response, time.time() - started

    # ------------------------------------------------------------ cases

    def health(self):
        r = requests.get(f"{self.base}/v1/api/health", timeout=5)
        self.check("GET /v1/api/health returns 200", r.status_code == 200, expected=200, actual=f"{r.status_code} {r.text}")

    def routed(self, name, event, expected_path):
        body, iv, tag = encrypt(event)
        r, elapsed = self.post_webhook(body, iv, tag)
        envelope = r.json() if r.headers.get("content-type", "").startswith("application/json") else {}
        self.check(f"{name}: acknowledged 200 with success envelope",
                   r.status_code == 200 and envelope.get("success") is True,
                   payload=event, expected="200 + success=true", actual=f"{r.status_code} {r.text}")
        self.check(f"{name}: acknowledged within {FAST_ACK_SECONDS}s", elapsed < FAST_ACK_SECONDS,
                   expected=f"< {FAST_ACK_SECONDS}s", actual=f"{elapsed:.3f}s")
        found = wait_for_delivery(body.encode())
        paths = [d["path"] for d in found]
        self.check(f"{name}: delivered to {expected_path}", paths == [expected_path],
                   payload=event, expected=[expected_path], actual=paths)
        return found, body, iv, tag

    def routing_cases(self):
        found, body, iv, tag = self.routed("menna-qa identifier", hyperpay_event("menna-qa"), "/qa")
        if found:
            headers = found[0]["headers"]
            self.check("forwarded body is byte-identical", found[0]["body"] == body.encode(),
                       expected=body[:40] + "...", actual=found[0]["body"][:40])
            self.check("X-Initialization-Vector forwarded unchanged", headers.get("x-initialization-vector") == iv,
                       expected=iv, actual=headers.get("x-initialization-vector"))
            self.check("X-Authentication-Tag forwarded unchanged", headers.get("x-authentication-tag") == tag,
                       expected=tag, actual=headers.get("x-authentication-tag"))
            self.check("Content-Type forwarded unchanged", headers.get("content-type") == "text/plain",
                       expected="text/plain", actual=headers.get("content-type"))
        self.routed("identifier is case-insensitive (MENNA-QA)", hyperpay_event("MENNA-QA"), "/qa")
        self.routed("unregistered identifier goes to default", hyperpay_event("menna-dev"), "/default")
        self.routed("no SHOPPER_service goes to default", hyperpay_event(None), "/default")
        self.routed("no customParameters goes to default", hyperpay_event(include_custom=False), "/default")

    def lowercase_headers_routed(self):
        event = hyperpay_event("menna-qa")
        body, iv, tag = encrypt(event)
        r, _ = self.post_webhook(body, iv, tag, lowercase_headers=True)
        found = wait_for_delivery(body.encode())
        self.check("BUG-01 lowercase x-initialization-vector/x-authentication-tag still decrypt and route",
                   r.status_code == 200 and [d["path"] for d in found] == ["/qa"],
                   payload=event, expected="200 then /qa", actual=f"{r.status_code} {[d['path'] for d in found]}")

    def json_wrapped_body(self):
        event = hyperpay_event("menna-qa")
        cipher, iv, tag = encrypt(event)
        body = json.dumps({"encryptedBody": cipher})
        r, _ = self.post_webhook(body, iv, tag, content_type="application/json")
        found = wait_for_delivery(body.encode())
        self.check("JSON-wrapped encryptedBody routes by identifier",
                   r.status_code == 200 and [d["path"] for d in found] == ["/qa"],
                   payload=body[:60], expected="200 then /qa", actual=f"{r.status_code} {[d['path'] for d in found]}")

    def bad_signature_dropped(self):
        body, iv, _ = encrypt(hyperpay_event("menna-qa"))
        r, _ = self.post_webhook(body, iv, "00" * 16)
        self.check("tampered tag: still acknowledged 200", r.status_code == 200, expected=200, actual=r.status_code)
        found = wait_for_delivery(body.encode(), timeout=NO_DELIVERY_WAIT_SECONDS)
        self.check("tampered tag: forwarded to nobody", found == [], expected=[], actual=[d["path"] for d in found])

    def wrong_secret_dropped(self):
        body, iv, tag = encrypt(hyperpay_event("menna-qa"), secret="ab" * 32)
        self.post_webhook(body, iv, tag)
        found = wait_for_delivery(body.encode(), timeout=NO_DELIVERY_WAIT_SECONDS)
        self.check("encrypted with another secret: forwarded to nobody", found == [], expected=[], actual=[d["path"] for d in found])

    def non_json_plaintext_dropped(self):
        body, iv, tag = encrypt(b"not json at all")
        r, _ = self.post_webhook(body, iv, tag)
        found = wait_for_delivery(body.encode(), timeout=NO_DELIVERY_WAIT_SECONDS)
        self.check("decrypts to non-JSON: acknowledged and forwarded to nobody",
                   r.status_code == 200 and found == [], expected="200 and []", actual=f"{r.status_code} {[d['path'] for d in found]}")

    def rejected_at_edge(self):
        r, _ = self.post_webhook("", "AA", "BB")
        body = r.json() if r.content else {}
        self.check("empty body rejected with 400 envelope", r.status_code == 400 and body.get("success") is False,
                   payload="''", expected="400 success=false", actual=f"{r.status_code} {r.text}")
        cipher, _, tag = encrypt(hyperpay_event("menna-qa"))
        r, _ = self.post_webhook(cipher, None, tag)
        self.check("missing X-Initialization-Vector rejected with 400", r.status_code == 400,
                   expected=400, actual=f"{r.status_code} {r.text}")
        r, _ = self.post_webhook(cipher, "AA", None)
        self.check("missing X-Authentication-Tag rejected with 400", r.status_code == 400,
                   expected=400, actual=f"{r.status_code} {r.text}")

    def retry_on_target_failure(self):
        STATE.fail_remaining["/flaky"] = 1
        event = hyperpay_event("menna-flaky")
        body, iv, tag = encrypt(event)
        r, _ = self.post_webhook(body, iv, tag)
        deadline = time.time() + DELIVERY_WAIT_SECONDS + 10
        attempts = []
        while time.time() < deadline:
            attempts = STATE.deliveries(body.encode())
            if len(attempts) >= 2:
                break
            time.sleep(0.2)
        self.check("target answers 500 once: Hangfire retries and the second attempt lands",
                   r.status_code == 200 and len(attempts) == 2 and all(a["path"] == "/flaky" for a in attempts),
                   payload=event, expected="2 attempts on /flaky", actual=[a["path"] for a in attempts])
        if len(attempts) == 2:
            gap = attempts[1]["at"] - attempts[0]["at"]
            self.check("BUG-02 configured RetryDelaysInSeconds (1s) is used, not the built-in default",
                       gap < RETRY_GAP_LIMIT_SECONDS, expected=f"< {RETRY_GAP_LIMIT_SECONDS}s", actual=f"{gap:.1f}s")

    def warm_up(self):
        body, iv, _ = encrypt(hyperpay_event("warm-up"))
        self.post_webhook(body, iv, "00" * 16)

    def run(self):
        self.health()
        self.warm_up()
        self.routing_cases()
        self.lowercase_headers_routed()
        self.json_wrapped_body()
        self.rejected_at_edge()
        self.bad_signature_dropped()
        self.wrong_secret_dropped()
        self.non_json_plaintext_dropped()
        self.retry_on_target_failure()


# ---------------------------------------------------------------- router process

def router_env(router_port, mock_port, overrides=None):
    mock = f"http://127.0.0.1:{mock_port}"
    env = os.environ.copy()
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": f"http://127.0.0.1:{router_port}",
        "HyperPay__WebhookSecretEnabled": "true",
        "HyperPay__WebhookSecret": TEST_SECRET,
        "Routing__DefaultUrl": f"{mock}/default",
        "Routing__Services__0__Identifier": "menna-qa",
        "Routing__Services__0__Url": f"{mock}/qa",
        "Routing__Services__1__Identifier": "menna-flaky",
        "Routing__Services__1__Url": f"{mock}/flaky",
        "Routing__TimeoutSeconds": "5",
        "Hangfire__RedisPrefix": "webhookrouter:test:",
        "Hangfire__RetryAttempts": "3",
        "Hangfire__RetryDelaysInSeconds__0": "1",
        "Hangfire__RetryDelaysInSeconds__1": "1",
        "Hangfire__RetryDelaysInSeconds__2": "1",
        "Hangfire__SchedulePollingSeconds": "1",
        "Hangfire__DashboardEnabled": "false",
    })
    env.update(overrides or {})
    return env


def start_router(router_port, mock_port, log_path, overrides=None, build=True):
    if build:
        subprocess.run(["dotnet", "build", str(PROJECT), "-v", "quiet", "-nologo"], check=True, cwd=ROOT,
                       stdout=subprocess.DEVNULL)
    log = open(log_path, "w")
    process = subprocess.Popen(["dotnet", "run", "--no-build", "--no-launch-profile", "--project", str(PROJECT)],
                               cwd=ROOT, env=router_env(router_port, mock_port, overrides), stdout=log, stderr=subprocess.STDOUT)
    base = f"http://127.0.0.1:{router_port}"
    deadline = time.time() + 60
    while time.time() < deadline:
        if process.poll() is not None:
            break
        try:
            if requests.get(f"{base}/v1/api/health", timeout=1).status_code == 200:
                return process, base
        except requests.RequestException:
            time.sleep(0.5)
    process.kill()
    log.close()
    print(Path(log_path).read_text()[-3000:])
    raise SystemExit("Router did not become healthy")


def stop_router(process):
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()


def enabled_without_secret_refuses_to_start(runner, args):
    env = router_env(args.router_port + 2, args.mock_port,
                     {"HyperPay__WebhookSecretEnabled": "true", "HyperPay__WebhookSecret": ""})
    result = subprocess.run(["dotnet", "run", "--no-build", "--no-launch-profile", "--project", str(PROJECT)],
                            cwd=ROOT, env=env, capture_output=True, text=True, timeout=60)
    output = result.stdout + result.stderr
    runner.check("WebhookSecretEnabled=true without a secret: router refuses to start",
                 result.returncode != 0 and "HyperPay:WebhookSecret must be" in output,
                 expected="non-zero exit with WebhookSecret validation message",
                 actual=f"exit {result.returncode}: {output[-300:]}")


def no_secret_forwards_to_default(runner, args):
    overrides = {"HyperPay__WebhookSecretEnabled": "false", "HyperPay__WebhookSecret": "",
                 "Hangfire__RedisPrefix": "webhookrouter:test-nosecret:"}
    log_path = str(Path(args.log).with_name("router-test-nosecret.log"))
    process, base = start_router(args.router_port + 1, args.mock_port, log_path, overrides, build=False)
    try:
        runner.check("WebhookSecretEnabled=false without a secret: router starts", True)
        no_secret = Runner(base)
        no_secret.passed = no_secret.failed = 0
        body, iv, tag = encrypt(hyperpay_event("menna-qa"))
        r, _ = no_secret.post_webhook(body, iv, tag)
        found = wait_for_delivery(body.encode())
        runner.check("WebhookSecretEnabled=false: webhook forwarded unread to default",
                     r.status_code == 200 and [d["path"] for d in found] == ["/default"],
                     expected="200 then /default", actual=f"{r.status_code} {[d['path'] for d in found]}")
        if found:
            runner.check("WebhookSecretEnabled=false: body and IV/tag forwarded unchanged",
                         found[0]["body"] == body.encode()
                         and found[0]["headers"].get("x-initialization-vector") == iv
                         and found[0]["headers"].get("x-authentication-tag") == tag,
                         expected="identical", actual="differs")
    finally:
        stop_router(process)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default=os.environ.get("WEBHOOK_ROUTER_BASE"),
                        help="Use an already running router (it must use the env from router_env) instead of starting one")
    parser.add_argument("--router-port", type=int, default=5190)
    parser.add_argument("--mock-port", type=int, default=5901)
    parser.add_argument("--log", default=str(Path(__file__).resolve().parent / "router-test.log"))
    args = parser.parse_args()

    mock = start_mock(args.mock_port)
    process = None
    try:
        if args.base:
            base = args.base
        else:
            process, base = start_router(args.router_port, args.mock_port, args.log)
        runner = Runner(base)
        runner.run()
        if process is not None:
            stop_router(process)
            process = None
            no_secret_forwards_to_default(runner, args)
            enabled_without_secret_refuses_to_start(runner, args)
        else:
            runner.skip("WebhookSecretEnabled cases", "needs the runner to start its own router")
    finally:
        if process is not None:
            stop_router(process)
        mock.shutdown()

    print()
    print(f"SUMMARY: {runner.passed} passed, {runner.failed} failed, {runner.skipped} skipped")
    sys.exit(runner.failed)


if __name__ == "__main__":
    main()
