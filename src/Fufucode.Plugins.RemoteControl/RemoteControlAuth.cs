using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Core.RemoteControl;

/// <summary>PIN 配对结果。</summary>
public sealed record RemotePairResult(bool Success, string? Token, string? Error, bool LockedOut);

/// <summary>
/// 4 位 PIN 与短期令牌管理。服务启动生成随机 PIN；配对成功后颁发短期令牌，
/// 未携带有效令牌的请求一律拒绝。连续错误 PIN 达到上限后按客户端锁定。
/// </summary>
public sealed class RemoteControlAuth
{
    private sealed class AttemptWindow
    {
        public int Count;
        public DateTimeOffset FirstAttemptUtc;
        public DateTimeOffset? LockedUntilUtc;
    }

    private readonly RemoteControlOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptWindow> _attempts = new(StringComparer.Ordinal);
    private string _pin;

    public RemoteControlAuth(RemoteControlOptions options, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.TokenLifetime <= TimeSpan.Zero || options.PinLockout <= TimeSpan.Zero || options.MaxPinAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "令牌有效期、锁定时长和重试次数必须为正数。");
        _clock = clock ?? TimeProvider.System;
        _pin = GeneratePin();
    }

    public string Pin
    {
        get { lock (_gate) return _pin; }
    }

    public TimeSpan TokenLifetime => _options.TokenLifetime;

    public int ActiveTokenCount
    {
        get { lock (_gate) { PurgeExpired(); return _tokens.Count; } }
    }

    /// <summary>重新生成 PIN 并撤销全部既有令牌（服务每次启动调用）。</summary>
    public string Regenerate()
    {
        lock (_gate)
        {
            _pin = GeneratePin();
            _tokens.Clear();
            _attempts.Clear();
            return _pin;
        }
    }

    public RemotePairResult Pair(string? pin, string clientKey)
    {
        lock (_gate)
        {
            PurgeExpired();
            var key = string.IsNullOrWhiteSpace(clientKey) ? "local" : clientKey;
            var now = _clock.GetUtcNow();
            var window = _attempts.TryGetValue(key, out var existing) ? existing : null;

            if (window is not null)
            {
                if (window.LockedUntilUtc is { } until && until > now)
                    return new RemotePairResult(false, null, "尝试次数过多，请稍后再试。", true);
                if ((window.LockedUntilUtc is { } expired && expired <= now) ||
                    now - window.FirstAttemptUtc > _options.PinLockout)
                {
                    _attempts.Remove(key);
                    window = null;
                }
            }

            if (!MatchesPin(pin))
            {
                var active = window ??= new AttemptWindow { FirstAttemptUtc = now };
                active.Count++;
                if (active.Count >= _options.MaxPinAttempts)
                    active.LockedUntilUtc = now + _options.PinLockout;
                _attempts[key] = active;
                var locked = active.LockedUntilUtc is not null;
                var remaining = Math.Max(0, _options.MaxPinAttempts - active.Count);
                return new RemotePairResult(false, null,
                    locked ? "尝试次数过多，请稍后再试。" : $"配对码不正确，还可尝试 {remaining} 次。", locked);
            }

            _attempts.Remove(key);
            var token = GenerateToken();
            _tokens[token] = now + _options.TokenLifetime;
            return new RemotePairResult(true, token, null, false);
        }
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_gate)
        {
            PurgeExpired();
            return _tokens.ContainsKey(token);
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _tokens.Clear();
            _attempts.Clear();
        }
    }

    private bool MatchesPin(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var expected = Encoding.UTF8.GetBytes(_pin);
        var actual = Encoding.UTF8.GetBytes(candidate.Trim());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private void PurgeExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var token in _tokens.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            _tokens.Remove(token);
    }

    private static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4", CultureInfo.InvariantCulture);

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
