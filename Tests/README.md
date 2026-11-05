# API tests

`api_tests.py` is an end-to-end runner. It:

- starts mock downstream services on `127.0.0.1:5901` (`/qa`, `/default`, `/flaky`),
- builds the router and launches it on `127.0.0.1:5190` with test settings: the test secret, routes pointing at the mocks, Redis prefix `webhookrouter:test:`, and 1-second retry delays,
- sends AES-GCM encrypted HyperPay webhooks and checks where each one lands.

## Requirements

- the .NET 10 SDK
- Redis on `localhost:6379`, or set `ConnectionStrings__Redis`
- Python 3 with `requests` and `cryptography`

```bash
python3 -m venv Tests/.venv
Tests/.venv/bin/pip install requests cryptography
```

## Run

```bash
Tests/.venv/bin/python Tests/api_tests.py
```

The router's output goes to `Tests/router-test.log`. Each check prints `[PASS]`, `[FAIL]` or `[SKIP]`, and a failure also shows the payload, the expected result and the actual result. The run ends with a `SUMMARY` line, and the exit code is the number of failures.

Options:

- `--router-port` and `--mock-port` change the ports.
- `--base http://host:port` (or `WEBHOOK_ROUTER_BASE`) targets a router that is already running. That router must use the same environment `router_env()` sets.

## What is covered

- `menna-qa` goes to its service, including a case variant. An unregistered identifier, a missing `SHOPPER_service` and missing `customParameters` all go to the default URL.
- The body, `X-Initialization-Vector`, `X-Authentication-Tag` and `Content-Type` are forwarded byte-identical.
- The acknowledgement is a 200 envelope returned in under 0.5 s.
- A JSON-wrapped `{"encryptedBody": ...}` body is routed.
- An empty body, or a missing IV or tag header, is rejected with 400.
- A tampered tag, a wrong secret, or a plaintext that is not JSON is acknowledged but forwarded to nobody.
- Regression BUG-01: lowercase IV and tag headers still decrypt, because the headers lose their case-insensitive comparer when Hangfire serializes the job.
- Regression BUG-02: the configured `RetryDelaysInSeconds` is the one actually used, rather than being appended to the built-in defaults.
- When a target answers 500, Hangfire retries and the delivery lands.
