using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Kafka;

namespace Lexfield.Ops.Tests;

/// <summary>
/// Runs a script in <c>scripts/ops/</c> the way an operator does: as a process,
/// with an environment and arguments, judged by its exit code and its output.
/// </summary>
public static class OperatorScript
{
    /// <summary>The repository root, found by walking up to the file that marks it.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public sealed record Result(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + StandardError;
    }

    public static async Task<Result> RunAsync(
        string script,
        IEnumerable<string>? arguments = null,
        IDictionary<string, string>? environment = null,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", "ops", script));
        foreach (var argument in arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// Drives the token inspector through its only input boundary, standard input,
/// and judges the allowlisted summary an operator can post as live evidence.
/// </summary>
public sealed class TaskApiTokenInspectionTests
{
    [Fact]
    public async Task Delegated_token_prints_only_the_allowlisted_summary()
    {
        var token = CreateJwt(new
        {
            ver = "2.0",
            idtyp = "user",
            tid = "tenant-secret-value",
            oid = "user-secret-value",
            sub = "pairwise-secret-value",
            azp = "client-secret-value",
            scp = "Tasks.Write profile",
            aud = "audience-secret-value",
            name = "person-secret-value",
        });

        var result = await OperatorScript.RunAsync(
            "inspect-taskapi-token.sh",
            standardInput: token + '\n');

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Join('\n',
        [
            "inspection: JWT payload decoded without signature validation",
            "token_version: \"2.0\"",
            "idtyp: \"user\"",
            "tid_present: true",
            "oid_present: true",
            "azp_present: true",
            "appid_present: false",
            "scp: [\"Tasks.Write\", \"profile\"]",
            "roles: []",
            "sub_equals_oid: false",
            "",
        ]), result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.DoesNotContain("tenant-secret-value", result.Output);
        Assert.DoesNotContain("user-secret-value", result.Output);
        Assert.DoesNotContain("pairwise-secret-value", result.Output);
        Assert.DoesNotContain("client-secret-value", result.Output);
        Assert.DoesNotContain("audience-secret-value", result.Output);
        Assert.DoesNotContain("person-secret-value", result.Output);
        Assert.DoesNotContain(token, result.Output);
    }

    [Fact]
    public async Task Application_token_reports_role_and_matching_subject_without_identifiers()
    {
        var token = CreateJwt(new
        {
            ver = "2.0",
            idtyp = "app",
            tid = "application-tenant-secret",
            oid = "workload-object-secret",
            sub = "workload-object-secret",
            appid = "workload-client-secret",
            roles = new[] { "Tasks.Write.All" },
        });

        var result = await OperatorScript.RunAsync(
            "inspect-taskapi-token.sh",
            standardInput: token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("idtyp: \"app\"", result.StandardOutput);
        Assert.Contains("azp_present: false", result.StandardOutput);
        Assert.Contains("appid_present: true", result.StandardOutput);
        Assert.Contains("scp: []", result.StandardOutput);
        Assert.Contains("roles: [\"Tasks.Write.All\"]", result.StandardOutput);
        Assert.Contains("sub_equals_oid: true", result.StandardOutput);
    }

    [Fact]
    public async Task Missing_optional_claims_have_explicit_empty_or_false_values()
    {
        var result = await OperatorScript.RunAsync(
            "inspect-taskapi-token.sh",
            standardInput: CreateJwt(new { }));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("idtyp: null", result.StandardOutput);
        Assert.Contains("tid_present: false", result.StandardOutput);
        Assert.Contains("oid_present: false", result.StandardOutput);
        Assert.Contains("scp: []", result.StandardOutput);
        Assert.Contains("roles: []", result.StandardOutput);
        Assert.Contains("sub_equals_oid: false", result.StandardOutput);
    }

    [Fact]
    public async Task Malformed_token_fails_before_printing_payload_fragments()
    {
        var malformed = CreateJwt("payload-fragment-that-must-not-print");

        var result = await OperatorScript.RunAsync(
            "inspect-taskapi-token.sh",
            standardInput: malformed);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.StartsWith("FAIL:", result.StandardError);
        Assert.DoesNotContain("payload-fragment-that-must-not-print", result.Output);
        Assert.DoesNotContain(malformed, result.Output);
    }

    [Fact]
    public async Task Token_in_a_command_argument_is_rejected_without_echoing_it()
    {
        var token = CreateJwt(new { tid = "argument-tenant-secret" });

        var result = await OperatorScript.RunAsync(
            "inspect-taskapi-token.sh",
            arguments: [token],
            standardInput: "ignored-standard-input");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("only through standard input", result.StandardError);
        Assert.DoesNotContain(token, result.Output);
        Assert.DoesNotContain("argument-tenant-secret", result.Output);
    }

    private static string CreateJwt(object payload)
    {
        static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{Encode(new { alg = "RS256", typ = "JWT" })}.{Encode(payload)}.synthetic-signature";
    }
}

/// <summary>
/// Exercises delegated capture as an operator process with synthetic HTTP replies.
/// </summary>
public sealed class TaskApiDelegatedCaptureTests
{
    [Theory]
    [InlineData("\"interval\":1", "\"interval\":null")]
    [InlineData("\"interval\":1", "\"interval\":0")]
    [InlineData("\"interval\":1", "\"interval\":\"5\"")]
    [InlineData("https://microsoft.com/devicelogin", "https://[private-invalid")]
    public async Task Invalid_field_in_otherwise_valid_device_response_never_reaches_polling(string field, string invalid)
    {
        using var protocol = new CaptureProtocol(DeviceReply() with { Body = DeviceReply().Body.Replace(field, invalid) });
        var result = await protocol.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.StartsWith("FAIL:", result.StandardError);
        Assert.DoesNotContain("Traceback", result.Output);
        Assert.DoesNotContain("private-", result.Output);
        Assert.Single(protocol.Requests());
    }

    [Fact]
    public async Task Second_sign_in_failure_discards_the_first_summary()
    {
        using var protocol = new CaptureProtocol(DeviceReply(),
            new Reply("""{"access_token":"e30.e30.synthetic-signature"}"""),
            new Reply("""{"error":"invalid_scope"}""", 400));
        var result = await protocol.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("invalid_scope", result.StandardError);
        Assert.Equal(3, protocol.Requests().Length);
    }

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("user_client_id")]
    [InlineData("taskapi_resource")]
    public async Task Missing_input_fails_before_any_request(string missing)
    {
        using var protocol = new CaptureProtocol();
        protocol.Environment[missing] = "";
        var result = await protocol.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missing + " is missing", result.StandardError);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(protocol.Requests());
    }

    [Theory]
    [InlineData("{\"error\":\"authorization_declined\",\"error_description\":\"private-error\"}", 400, 0, false)]
    [InlineData("{\"error\":\"expired_token\"}", 400, 0, false)]
    [InlineData("{\"error\":\"bad_verification_code\"}", 400, 0, false)]
    [InlineData("{\"error\":\"private-unknown-error\"}", 400, 0, false)]
    [InlineData("<html>private-error</html>", 502, 0, false)]
    [InlineData("{}", 200, 0, false)]
    [InlineData("{\"access_token\":\"private-malformed-token\"}", 200, 0, false)]
    [InlineData("{\"access_token\":\"\\ud800\"}", 200, 0, false)]
    [InlineData("private-invalid-utf8", 200, 0, true)]
    [InlineData("private-transport-output", 0, 28, false)]
    public async Task Failed_poll_stops_before_second_capture_without_publishing_partial_evidence(
        string body, int httpStatus, int exitCode, bool malformedUtf8)
    {
        using var protocol = new CaptureProtocol(DeviceReply(), new Reply(body, httpStatus, exitCode, malformedUtf8));
        var result = await protocol.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("FAIL:", result.StandardError);
        Assert.DoesNotContain("private-", result.Output);
        Assert.DoesNotContain("Traceback", result.Output);
        Assert.Equal(2, protocol.Requests().Length);
    }

    [Fact]
    public async Task Expiry_before_next_poll_stops_instead_of_waiting_or_requesting_a_token()
    {
        using var protocol = new CaptureProtocol(DeviceReply() with
        {
            Body = DeviceReply().Body.Replace("\"expires_in\":30", "\"expires_in\":1"),
        });
        var result = await protocol.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("expired", result.StandardError);
        Assert.Empty(result.StandardOutput);
        Assert.Single(protocol.Requests());
    }

    [Theory]
    [InlineData("https://microsoft.com/devicelogin")]
    [InlineData("https://login.microsoft.com/device")]
    public async Task Two_captures_keep_openid_constant_and_emit_only_inspector_summaries(string verificationUri)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"ver":"2.0","idtyp":"user","tid":"private-tenant","oid":"private-user","scp":"Tasks.Write"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = "e30." + payload + ".synthetic-signature";
        var granted = new Reply(JsonSerializer.Serialize(new { access_token = token, id_token = "private-id-token" }));
        using var protocol = new CaptureProtocol(DeviceReply(verificationUri),
            new Reply("""{"error":"authorization_pending"}""", 400), granted, DeviceReply(verificationUri), granted);

        var result = await protocol.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("**Delegated Tasks.Write openid profile:**", result.StandardOutput);
        Assert.Contains("**Delegated Tasks.Write openid (without profile):**", result.StandardOutput);
        Assert.Equal(2, result.StandardOutput.Split("inspection:").Length - 1);
        Assert.Contains("scp: [\"Tasks.Write\"]", result.StandardOutput);
        Assert.Contains("TESTCODE", result.StandardError);
        Assert.Equal(2, result.StandardError.Split("open " + verificationUri + " and enter TESTCODE").Length - 1);
        Assert.DoesNotContain("private-", result.Output);
        Assert.DoesNotContain(token, result.Output);
        var requests = protocol.Requests();
        Assert.Equal(5, requests.Length);
        Assert.True(requests[1].GetProperty("at").GetDouble() - requests[0].GetProperty("at").GetDouble() >= 1);
        Assert.True(requests[2].GetProperty("at").GetDouble() - requests[1].GetProperty("at").GetDouble() >= 1);
        Assert.Equal("api://synthetic-task-api/Tasks.Write openid profile", requests[0].GetProperty("form").GetProperty("scope")[0].GetString());
        Assert.Equal("api://synthetic-task-api/Tasks.Write openid", requests[3].GetProperty("form").GetProperty("scope")[0].GetString());
        foreach (var request in new[] { requests[1], requests[2], requests[4] })
        {
            var form = request.GetProperty("form");
            Assert.False(form.TryGetProperty("scope", out _));
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form.GetProperty("grant_type")[0].GetString());
            Assert.Equal("private-device", form.GetProperty("device_code")[0].GetString());
            Assert.DoesNotContain("private-device", request.GetProperty("arguments").ToString());
        }
    }

    private static Reply DeviceReply(string verificationUri = "https://microsoft.com/devicelogin") => new(
        JsonSerializer.Serialize(new { device_code = "private-device", user_code = "TESTCODE", verification_uri = verificationUri, interval = 1, expires_in = 30 }));

    [Theory]
    [InlineData("http://login.microsoft.com/device")]
    [InlineData("https://untrusted.example/device")]
    [InlineData("https://login.microsoft.com.untrusted.example/device")]
    [InlineData("https://login.microsoft.com@untrusted.example/device")]
    [InlineData("https://untrusted.example@login.microsoft.com/device")]
    [InlineData("https://login.microsoft.com:443/device")]
    public async Task Unapproved_sign_in_url_stops_before_polling_or_displaying_codes(string verificationUri)
    {
        using var protocol = new CaptureProtocol(DeviceReply(verificationUri));
        var result = await protocol.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("FAIL: device authorization sign-in instructions are invalid\n", result.StandardError);
        Assert.DoesNotContain("TESTCODE", result.Output);
        Assert.DoesNotContain("private-device", result.Output);
        Assert.DoesNotContain(verificationUri, result.Output);
        Assert.Single(protocol.Requests());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"device_code\":\"private-device\",\"interval\":null}")]
    [InlineData("{\"device_code\":\"private-device\",\"interval\":0}")]
    [InlineData("{\"device_code\":\"private-device\",\"interval\":\"5\"}")]
    public async Task Invalid_device_response_stops_without_polling_or_printing_private_fields(string body)
    {
        using var protocol = new CaptureProtocol(new Reply(body));
        var result = await protocol.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.StartsWith("FAIL:", result.StandardError);
        Assert.DoesNotContain("private-device", result.Output);
        Assert.DoesNotContain("Traceback", result.Output);
        Assert.Single(protocol.Requests());
    }

    [Fact]
    public async Task Invalid_scope_stops_before_polling_and_does_not_print_the_response_body()
    {
        using var protocol = new CaptureProtocol(new Reply(
            """{"error":"invalid_scope","error_codes":[70011],"error_description":"private-diagnostic"}""", 400));

        var result = await protocol.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("FAIL: device authorization error=invalid_scope error_codes=[70011]", result.StandardError);
        Assert.DoesNotContain("private-diagnostic", result.Output);
        Assert.DoesNotContain("Traceback", result.Output);
        Assert.Single(protocol.Requests());
    }

    private sealed record Reply(string Body, int HttpStatus = 200, int ExitCode = 0, bool MalformedUtf8 = false);

    // Only the external HTTP command is replaced. The capture process and
    // inspector are real, and all protocol fixtures and identifiers are synthetic.
    private sealed class CaptureProtocol : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("taskapi-capture-").FullName;

        public Dictionary<string, string> Environment { get; }

        public CaptureProtocol(params Reply[] replies)
        {
            File.WriteAllText(Path.Combine(_directory, "responses.json"), JsonSerializer.Serialize(replies));
            var stub = Path.Combine(_directory, "curl");
            File.WriteAllText(stub, """
                #!/usr/bin/env python3
                import json, pathlib, sys, time
                from urllib.parse import parse_qs
                root = pathlib.Path(__file__).parent
                log = root / "requests.json"
                requests = json.loads(log.read_text()) if log.exists() else []
                requests.append({"arguments": sys.argv[1:], "form": parse_qs(sys.stdin.read()), "at": time.monotonic()})
                log.write_text(json.dumps(requests))
                replies = json.loads((root / "responses.json").read_text())
                if len(requests) > len(replies):
                    sys.exit(99)
                reply = replies[len(requests) - 1]
                output = (reply["Body"] + "\n" + str(reply["HttpStatus"])).encode("utf-8")
                sys.stdout.buffer.write(b"\xff" if reply["MalformedUtf8"] else output)
                sys.exit(reply["ExitCode"])
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            Environment = new Dictionary<string, string>
            {
                ["PATH"] = _directory + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH"),
                ["tenant_id"] = "11111111-1111-1111-1111-111111111111",
                ["user_client_id"] = "22222222-2222-2222-2222-222222222222",
                ["taskapi_resource"] = "api://synthetic-task-api",
            };
        }

        public Task<OperatorScript.Result> RunAsync() => OperatorScript.RunAsync(
            "capture-taskapi-delegated-tokens.sh", environment: Environment);

        public JsonElement[] Requests() => File.Exists(Path.Combine(_directory, "requests.json"))
            ? JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(_directory, "requests.json")))!
            : [];

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// Runs the documented ACI procedure with a synthetic Azure CLI. The installed
/// program and inspector are real; metadata and Azure resource state are not.
/// </summary>
public sealed class TaskApiWorkloadCaptureTests
{
    [Fact]
    public async Task Prepared_program_runs_in_the_documented_image_without_startup_logs()
    {
        var prepared = await OperatorScript.RunAsync("capture-taskapi-workload-token.sh", ["prepare"]);
        Assert.Equal(0, prepared.ExitCode);
        await using var container = new ContainerBuilder("mcr.microsoft.com/azure-cli:azurelinux3.0")
            .WithEntrypoint("/bin/bash")
            .WithCommand("-c", prepared.StandardOutput.Trim())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilFileExists("/tmp/taskapi-token",
                DotNet.Testcontainers.Configurations.FileSystem.Container,
                wait => wait.WithTimeout(TimeSpan.FromSeconds(30))))
            .Build();
        await container.StartAsync();
        // Empty stdin stops before metadata HTTP. No Azure token is requested.
        var result = await container.ExecAsync(["/bin/bash", "-c", "/tmp/taskapi-token </dev/null"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("READY", result.Stdout);
        Assert.Contains("response suppressed", result.Stderr);
        var logs = await container.GetLogsAsync();
        Assert.Empty(logs.Stdout + logs.Stderr);
    }

    [Fact]
    public async Task Runbook_captures_through_a_single_executable_and_verifies_cleanup()
    {
        var result = await RunAsync("success");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("roles: [\"Tasks.Write.All\"]", result.StandardOutput);
        Assert.Contains("temporary ACI deletion readback=pass", result.Output);
        Assert.DoesNotContain("private-", result.Output);
    }

    [Theory]
    [InlineData("exec-failure")]
    [InlineData("bad-ready")]
    [InlineData("malformed-token")]
    [InlineData("metadata-failure")]
    [InlineData("logs")]
    [InlineData("create-failure")]
    public async Task Failed_capture_publishes_no_summary_and_still_deletes_the_group(string scenario)
    {
        var result = await RunAsync(scenario);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FAIL:", result.StandardError);
        Assert.DoesNotContain("inspection:", result.Output);
        Assert.DoesNotContain("private-", result.Output);
        Assert.Contains("temporary ACI deletion readback=pass", result.Output);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("delete-failure")]
    public async Task Existing_group_or_failed_cleanup_never_publishes_completion(string scenario)
    {
        var result = await RunAsync(scenario);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FAIL:", result.StandardError);
        Assert.DoesNotContain("inspection:", result.Output);
        Assert.DoesNotContain("deletion readback=pass", result.Output);
        Assert.DoesNotContain("private-", result.Output);
    }

    private static async Task<OperatorScript.Result> RunAsync(string scenario)
    {
        var directory = Directory.CreateTempSubdirectory("taskapi-workload-").FullName;
        try
        {
            // Replace only the metadata HTTP boundary, using synthetic claims.
            await File.WriteAllTextAsync(Path.Combine(directory, "sitecustomize.py"), """
                import base64, io, json, os, urllib.request
                from urllib.parse import parse_qs, urlsplit
                def metadata(self, request, **kwargs):
                    url = urlsplit(request.full_url)
                    assert url.hostname == '169.254.169.254'
                    assert url.path == '/metadata/identity/oauth2/token'
                    assert parse_qs(url.query) == {
                        'resource': ['api://private-resource'],
                        'client_id': ['11111111-1111-1111-1111-111111111111'],
                        'api-version': ['2018-02-01']}
                    assert request.get_header('Metadata') == 'true'
                    if os.environ['SCENARIO'] == 'metadata-failure':
                        raise ValueError('private-response-must-not-escape')
                    payload = base64.urlsafe_b64encode(json.dumps({
                        'idtyp': 'app', 'tid': 'private-tenant', 'oid': 'private-object',
                        'roles': ['Tasks.Write.All']}).encode()).decode().rstrip('=')
                    token = 'header.' + payload + '.signature'
                    if os.environ['SCENARIO'] == 'malformed-token':
                        token = 'private-invalid-token'
                    return io.BytesIO(json.dumps({'access_token': token}).encode())
                urllib.request.OpenerDirector.open = metadata
                """);
            var stub = Path.Combine(directory, "az");
            await File.WriteAllTextAsync(stub, """
                #!/usr/bin/env python3
                import os, pathlib, pty, shlex, subprocess, sys, threading, time
                root = pathlib.Path(__file__).parent
                args = sys.argv[1:]
                def value(flag):
                    return args[args.index(flag) + 1]
                state = root / 'created'
                if os.environ['SCENARIO'] == 'existing':
                    state.touch()
                if args[:2] == ['identity', 'show']:
                    print('synthetic-group' if value('--query') == 'resourceGroup' else 'uksouth')
                elif args[:2] == ['container', 'list']:
                    print(int(state.exists()))
                elif args[:2] == ['container', 'create']:
                    bootstrap = value('--command-line')
                    assert 'private-' not in bootstrap
                    assert '11111111-1111-1111-1111-111111111111' not in bootstrap
                    # Run the actual startup program in a private synthetic container filesystem.
                    bootstrap = bootstrap.replace('/tmp/taskapi-token', str(root / 'taskapi-token'))
                    boot = subprocess.Popen(shlex.split(bootstrap), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                    try:
                        for _ in range(100):
                            if (root / 'taskapi-token').exists():
                                break
                            time.sleep(0.01)
                    finally:
                        boot.kill()
                        out, err = boot.communicate()
                    assert not out and not err
                    assert (root / 'taskapi-token').exists()
                    assert 'private-' not in (root / 'taskapi-token').read_text()
                    state.touch()
                    if os.environ['SCENARIO'] == 'create-failure':
                        sys.exit('private-create-error')
                elif args[:2] == ['container', 'show']:
                    print('Running')
                elif args[:2] == ['container', 'exec']:
                    command = value('--exec-command')
                    if len(command.split()) != 1:
                        sys.exit('ACI exec does not support command arguments')
                    if os.environ['SCENARIO'] == 'exec-failure':
                        sys.exit('private-exec-error')
                    if os.environ['SCENARIO'] == 'bad-ready':
                        print('private-unexpected-output', flush=True)
                        sys.exit(0)
                    master, slave = pty.openpty()
                    env = dict(os.environ, PYTHONPATH=str(root))
                    child = subprocess.Popen([str(root / pathlib.Path(command).name)],
                        stdin=slave, stdout=slave, stderr=slave, env=env)
                    os.close(slave)
                    def forward_input():
                        line = sys.stdin.buffer.readline()
                        if line:
                            os.write(master, line)
                    threading.Thread(target=forward_input, daemon=True).start()
                    try:
                        while True:
                            chunk = os.read(master, 4096)
                            if not chunk:
                                break
                            sys.stdout.buffer.write(chunk)
                            sys.stdout.buffer.flush()
                    except OSError:
                        pass
                    finally:
                        os.close(master)
                        child.wait(timeout=5)
                    # ACI exec does not promise propagation of the remote exit code.
                elif args[:2] == ['container', 'delete']:
                    assert os.environ['SCENARIO'] != 'existing'
                    if os.environ['SCENARIO'] == 'delete-failure':
                        sys.exit('private-delete-error')
                    state.unlink()
                elif args[:2] == ['container', 'logs'] and os.environ['SCENARIO'] == 'logs':
                    print('private-log-content')
                elif args[:2] != ['container', 'logs']:
                    sys.exit(99)
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var runbook = await File.ReadAllTextAsync(Path.Combine(OperatorScript.RepositoryRoot,
                "docs/runbooks/verify-taskapi-token-claims.md"));
            var block = runbook.Split("## 3.")[1].Split("```bash\n")[1].Split("```")[0];
            var start = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = OperatorScript.RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("set -euo pipefail\nfail() { echo \"FAIL: $1\" >&2; exit 1; }\n"
                + block + "\nprintf '%s\\n' \"${workload_summary:-}\"");
            start.Environment["PATH"] = directory + Path.PathSeparator + start.Environment["PATH"];
            start.Environment["delegated_summary"] = "synthetic delegated summary";
            start.Environment["workload_resource_id"] = "synthetic-identity-resource";
            start.Environment["workload_client_id"] = "11111111-1111-1111-1111-111111111111";
            start.Environment["taskapi_resource"] = "api://private-resource";
            start.Environment["SCENARIO"] = scenario;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.Equal(scenario is "existing" or "delete-failure",
                File.Exists(Path.Combine(directory, "created")));
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

/// <summary>
/// Checks argument validation without a cluster.
/// </summary>
public sealed class OperatorScriptArgumentTests
{
    [Theory]
    [InlineData("pause-connector.sh", "<tenantId>")]
    [InlineData("resume-connector.sh", "<tenantId>")]
    [InlineData("notifier-control.sh", "<retry|skip> <partition> <offset> <reason>")]
    public async Task Names_its_required_arguments_and_fails_when_called_with_none(
        string script,
        string expectedArguments)
    {
        var result = await OperatorScript.RunAsync(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedArguments, result.Output);
        Assert.StartsWith("FAIL:", result.StandardError.TrimStart());
    }

    [Fact]
    public async Task Notifier_control_refuses_a_verb_the_notifier_does_not_have()
    {
        var result = await OperatorScript.RunAsync(
            "notifier-control.sh", ["park", "7", "4102", "operator typed the wrong verb"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("action must be retry or skip", result.Output);
        Assert.Contains("Valid form: notifier-control.sh <retry|skip> <partition> <offset> <reason>", result.Output);
    }

    [Theory]
    [InlineData("nope", "4102", "operator supplied an invalid number", "partition must be a non-negative integer", "a number such as 7")]
    [InlineData("7", "nope", "operator supplied an invalid number", "offset must be a non-negative integer", "a number such as 4102")]
    [InlineData("7", "4102", "   ", "reason must contain text", "downstream sender restored")]
    public async Task Notifier_control_names_invalid_input_and_shows_a_valid_form(
        string partition,
        string offset,
        string reason,
        string expectedProblem,
        string expectedForm)
    {
        var result = await OperatorScript.RunAsync(
            "notifier-control.sh", ["retry", partition, offset, reason]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedProblem, result.Output);
        Assert.Contains(expectedForm, result.Output);
    }

    [Theory]
    [InlineData("pause-connector.sh", "pause")]
    [InlineData("resume-connector.sh", "resume")]
    [InlineData("connector-target-state.sh", "pause")]
    public async Task Empty_tenant_id_fails_before_a_connect_request_and_explains_the_correction(
        string script,
        string verb)
    {
        var binDirectory = Directory.CreateTempSubdirectory("connect-bin").FullName;
        var requestMarker = Path.Combine(binDirectory, "curl-requested");
        var curlStub = Path.Combine(binDirectory, "curl");
        await File.WriteAllTextAsync(curlStub, $"#!/usr/bin/env bash\ntouch \"{requestMarker}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(curlStub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        string[] arguments = script == "connector-target-state.sh"
            ? [verb, ""]
            : [""];
        var result = await OperatorScript.RunAsync(
            script,
            arguments,
            new Dictionary<string, string> { ["PATH"] = binDirectory });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tenantId must not be empty", result.StandardError);
        Assert.Contains("Valid form:", result.StandardError);
        Assert.False(File.Exists(requestMarker));
    }

    [Fact]
    public async Task Notifier_control_writes_the_control_message_the_notifier_reads()
    {
        // A stub on PATH stands in for the Kafka CLI, so the assertion is on the
        // message and the destination rather than on a broker's acknowledgement.
        var binDirectory = Directory.CreateTempSubdirectory("kafka-bin").FullName;
        var recording = Path.Combine(binDirectory, "recorded.txt");
        var stub = Path.Combine(binDirectory, "kafka-console-producer.sh");
        await File.WriteAllTextAsync(stub, $"""
            #!/usr/bin/env bash
            echo "$@" > "{recording}"
            cat >> "{recording}"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var result = await OperatorScript.RunAsync(
            "notifier-control.sh",
            ["skip", "7", "4102", "malformed payload from the 09:40 deploy"],
            new Dictionary<string, string> { ["KAFKA_BIN_DIR"] = binDirectory });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: notifier consumer action 'skip'", result.StandardOutput);
        Assert.Contains("Kafka partition 7 at offset 4102", result.StandardOutput);
        Assert.Contains("success: notifier control action 'skip'", result.StandardOutput);
        var recorded = (await File.ReadAllLinesAsync(recording)).ToArray();
        Assert.Contains("--topic notifier-control", recorded[0]);
        using var message = JsonDocument.Parse(recorded[1]);
        Assert.Equal("skip", message.RootElement.GetProperty("action").GetString());
        Assert.Equal(7, message.RootElement.GetProperty("partition").GetInt32());
        Assert.Equal(4102, message.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(
            "malformed payload from the 09:40 deploy",
            message.RootElement.GetProperty("reason").GetString());
    }
}

/// <summary>
/// One Kafka broker and one Connect worker on a shared Docker network. Connect
/// reaches the broker by the network alias the extra listener registers; the test
/// process reaches Connect by its mapped port.
/// </summary>
public sealed class ConnectFixture : IAsyncLifetime
{
    private const string BrokerAlias = "kafka";
    private const int BrokerPort = 19092;

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly KafkaContainer _kafka;
    private readonly IContainer _connect;

    public ConnectFixture()
    {
        // Pinned to the same Confluent version as the shared Kafka fixture in
        // tests/Lexfield.TestSupport. The production worker is the Strimzi-based
        // image in connect/image/; nothing here tests that image, only the REST
        // API the scripts speak to.
        _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12")
            .WithNetwork(_network)
            .WithListener($"{BrokerAlias}:{BrokerPort}")
            .Build();

        _connect = new ContainerBuilder("confluentinc/cp-kafka-connect:7.5.12")
            .WithNetwork(_network)
            .WithPortBinding(8083, true)
            .WithEnvironment("CONNECT_BOOTSTRAP_SERVERS", $"{BrokerAlias}:{BrokerPort}")
            .WithEnvironment("CONNECT_REST_ADVERTISED_HOST_NAME", "connect")
            .WithEnvironment("CONNECT_GROUP_ID", "lexfield-ops-tests")
            .WithEnvironment("CONNECT_CONFIG_STORAGE_TOPIC", "ops-tests-configs")
            .WithEnvironment("CONNECT_OFFSET_STORAGE_TOPIC", "ops-tests-offsets")
            .WithEnvironment("CONNECT_STATUS_STORAGE_TOPIC", "ops-tests-status")
            .WithEnvironment("CONNECT_CONFIG_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_OFFSET_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_STATUS_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_KEY_CONVERTER", "org.apache.kafka.connect.storage.StringConverter")
            .WithEnvironment("CONNECT_VALUE_CONVERTER", "org.apache.kafka.connect.storage.StringConverter")
            // The FileStream connectors ship in the image but sit outside the
            // default plugin path, so the worker only finds them once the
            // directory is named here. This layout was read from the 8.3.x image
            // build; that it also holds on the 7.5.12 pinned above is evidenced
            // only by these tests passing, so re-check it when either image moves.
            .WithEnvironment(
                "CONNECT_PLUGIN_PATH",
                "/usr/share/java,/usr/share/confluent-hub-components,/usr/share/filestream-connectors")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8083).ForPath("/connectors")))
            .Build();
    }

    /// <summary>The value the scripts read from <c>CONNECT_URL</c>.</summary>
    public string ConnectUrl =>
        $"http://{_connect.Hostname}:{_connect.GetMappedPublicPort(8083)}";

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await _kafka.StartAsync();
        await _connect.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _connect.DisposeAsync();
        await _kafka.DisposeAsync();
        await _network.DeleteAsync();
    }

    /// <summary>
    /// Creates a connector named the way the generator names one, so the scripts
    /// derive the same name from a tenant id that they will in production.
    /// </summary>
    public async Task CreateConnectorAsync(string tenantId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ConnectUrl) };
        var response = await client.PostAsJsonAsync("/connectors", new
        {
            name = $"tenant-{tenantId}-outbox",
            config = new Dictionary<string, string>
            {
                ["connector.class"] = "org.apache.kafka.connect.file.FileStreamSourceConnector",
                ["tasks.max"] = "1",
                // An empty file keeps the task RUNNING with nothing to read, which
                // is all these tests need from it.
                ["file"] = "/dev/null",
                ["topic"] = $"ops-tests-{tenantId}",
            },
        });
        response.EnsureSuccessStatusCode();
        await WaitForStateAsync(tenantId, "RUNNING");
    }

    /// <summary>
    /// Reads the connector and task states Connect reports, or <c>UNKNOWN</c>
    /// while there is nothing to read yet.
    /// </summary>
    /// <remarks>
    /// Connect answers the create with 201 as soon as the configuration is
    /// recorded, and writes the connector's status afterwards, so
    /// <c>/status</c> answers 404 for a moment on a connector that does
    /// certainly exist. Treating that 404 as absent turns a startup race into a
    /// failed test, so it reads as "not visible yet" and the caller keeps
    /// polling until its own deadline.
    /// </remarks>
    public async Task<string> ReadStatesAsync(string tenantId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ConnectUrl) };
        using var response = await client.GetAsync($"/connectors/tenant-{tenantId}-outbox/status");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "UNKNOWN";
        }

        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var states = new StringBuilder(document.RootElement.GetProperty("connector").GetProperty("state").GetString());
        foreach (var task in document.RootElement.GetProperty("tasks").EnumerateArray())
        {
            states.Append(' ').Append(task.GetProperty("state").GetString());
        }

        return states.ToString();
    }

    private async Task WaitForStateAsync(string tenantId, string state)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        var observed = "nothing read yet";
        while (DateTime.UtcNow < deadline)
        {
            observed = await ReadStatesAsync(tenantId);
            var states = observed.Split(' ');

            // The first element is the connector and the rest are its tasks. A
            // connector reports its own state before its task is assigned, so
            // waiting on the connector alone would hand back a connector with no
            // task and the caller would then assert on a task that does not
            // exist yet.
            if (states.Length > 1 && states.All(each => each == state))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"tenant-{tenantId}-outbox did not reach {state}. Last seen: {observed}");
    }
}

/// <summary>
/// The behaviour the runbook step depends on: the pause script does not report
/// success on the accepted request, it reports the state Connect settles in and
/// names both the requested and observed states.
/// </summary>
public sealed class ConnectorScriptTests(ConnectFixture connect) : IClassFixture<ConnectFixture>
{
    private Dictionary<string, string> Environment => new()
    {
        ["CONNECT_URL"] = connect.ConnectUrl,
        ["CONNECT_TIMEOUT_SECONDS"] = "60",
    };

    [Fact]
    public async Task Pause_waits_for_the_paused_state_and_prints_it()
    {
        const string TenantId = "lexfield-pause";
        await connect.CreateConnectorAsync(TenantId);

        var result = await OperatorScript.RunAsync("pause-connector.sh", [TenantId], Environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: pause Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Requested state: PAUSED", result.StandardOutput);
        Assert.Contains("success: pause completed for Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Observed states:", result.StandardOutput);
        Assert.Contains("connector PAUSED", result.StandardOutput);
        Assert.Contains("task 0 PAUSED", result.StandardOutput);
        // Connect accepts the pause before the states change, so a script that
        // returned on the acknowledgement could pass the assertions above and
        // still leave the connector running. This reads the cluster afterwards.
        Assert.Equal("PAUSED PAUSED", await connect.ReadStatesAsync(TenantId));
    }

    [Fact]
    public async Task Resume_returns_the_connector_to_running()
    {
        const string TenantId = "lexfield-resume";
        await connect.CreateConnectorAsync(TenantId);
        await OperatorScript.RunAsync("pause-connector.sh", [TenantId], Environment);

        var result = await OperatorScript.RunAsync("resume-connector.sh", [TenantId], Environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: resume Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Requested state: RUNNING", result.StandardOutput);
        Assert.Contains("success: resume completed for Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Observed states:", result.StandardOutput);
        Assert.Contains("connector RUNNING", result.StandardOutput);
        Assert.Equal("RUNNING RUNNING", await connect.ReadStatesAsync(TenantId));
    }

    [Fact]
    public async Task Pause_fails_and_says_so_when_the_connector_does_not_exist()
    {
        var result = await OperatorScript.RunAsync(
            "pause-connector.sh", ["lexfield-absent"], Environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tenant-lexfield-absent-outbox does not exist", result.StandardError);
        Assert.Contains("Check the tenantId", result.StandardError);
    }
}
