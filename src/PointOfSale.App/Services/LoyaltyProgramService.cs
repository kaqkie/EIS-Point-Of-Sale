using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface ILoyaltyProgramService
{
    Task<LoyaltyMember> EnrollAsync(string fullName, string? phone, string? memberCode = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyMember>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<LoyaltyMember?> GetByCodeAsync(string memberCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyLedgerEntry>> GetLedgerAsync(int memberId, CancellationToken cancellationToken = default);
    decimal CalculateEarnPoints(decimal invoiceTotalMwk);
    decimal CalculateRedeemValueMwk(decimal points);
    Task<LoyaltyRedeemResult> RedeemAtCheckoutAsync(
        int memberId,
        decimal points,
        string? invoiceNumber,
        CancellationToken cancellationToken = default);
    Task EarnFromPurchaseAsync(
        int memberId,
        decimal invoiceTotalMwk,
        string? invoiceNumber,
        CancellationToken cancellationToken = default);
}

public sealed class LoyaltyRedeemResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public decimal PointsRedeemed { get; init; }
    public decimal DiscountMwk { get; init; }
    public decimal NewBalance { get; init; }

    public static LoyaltyRedeemResult Fail(string error) => new() { Success = false, Error = error };
}

public sealed class LoyaltyProgramService : ILoyaltyProgramService
{
    private readonly ILoyaltyMemberRepository _repository;
    private readonly LoyaltyProgramOptions _options;

    public LoyaltyProgramService(
        ILoyaltyMemberRepository repository,
        IOptions<LoyaltyProgramOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public async Task<LoyaltyMember> EnrollAsync(
        string fullName,
        string? phone,
        string? memberCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        var code = string.IsNullOrWhiteSpace(memberCode)
            ? $"LM-{DateTime.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(100, 999)}"
            : memberCode.Trim().ToUpperInvariant();

        if (await _repository.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException($"Member code '{code}' already exists.");
        }

        var member = new LoyaltyMember
        {
            MemberCode = code,
            FullName = fullName.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            PointsBalance = 0,
            LifetimeSpendMwk = 0,
            IsActive = true
        };
        member.MemberId = await _repository.CreateAsync(member, cancellationToken).ConfigureAwait(false);
        return member;
    }

    public Task<IReadOnlyList<LoyaltyMember>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<LoyaltyMember>>(Array.Empty<LoyaltyMember>());
        }

        return _repository.SearchAsync(query, 50, cancellationToken);
    }

    public Task<LoyaltyMember?> GetByCodeAsync(string memberCode, CancellationToken cancellationToken = default) =>
        _repository.GetByCodeAsync(memberCode.Trim().ToUpperInvariant(), cancellationToken);

    public Task<IReadOnlyList<LoyaltyLedgerEntry>> GetLedgerAsync(
        int memberId,
        CancellationToken cancellationToken = default) =>
        _repository.GetLedgerAsync(memberId, 50, cancellationToken);

    public decimal CalculateEarnPoints(decimal invoiceTotalMwk)
    {
        if (!_options.Enabled || invoiceTotalMwk <= 0 || _options.PointsPerThousandMwk <= 0)
        {
            return 0m;
        }

        var points = invoiceTotalMwk / 1000m * _options.PointsPerThousandMwk;
        return PosTaxCalculator.RoundMoney(points);
    }

    public decimal CalculateRedeemValueMwk(decimal points)
    {
        if (points <= 0 || _options.MwkPerRedeemedPoint <= 0)
        {
            return 0m;
        }

        return PosTaxCalculator.RoundMoney(points * _options.MwkPerRedeemedPoint);
    }

    public async Task<LoyaltyRedeemResult> RedeemAtCheckoutAsync(
        int memberId,
        decimal points,
        string? invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return LoyaltyRedeemResult.Fail("Loyalty program is disabled.");
        }

        points = PosTaxCalculator.RoundMoney(points);
        if (points < _options.MinimumRedeemPoints)
        {
            return LoyaltyRedeemResult.Fail($"Minimum redemption is {_options.MinimumRedeemPoints} points.");
        }

        var discount = CalculateRedeemValueMwk(points);
        var (ok, balance) = await _repository.TryRedeemPointsAsync(
                memberId,
                points,
                discount,
                invoiceNumber,
                notes: "Checkout redemption",
                cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            return LoyaltyRedeemResult.Fail("Insufficient points balance or inactive member.");
        }

        return new LoyaltyRedeemResult
        {
            Success = true,
            PointsRedeemed = points,
            DiscountMwk = discount,
            NewBalance = balance
        };
    }

    public async Task EarnFromPurchaseAsync(
        int memberId,
        decimal invoiceTotalMwk,
        string? invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        var points = CalculateEarnPoints(invoiceTotalMwk);
        if (points <= 0)
        {
            return;
        }

        await _repository.EarnPointsAsync(
                memberId,
                points,
                invoiceTotalMwk,
                invoiceNumber,
                notes: "Purchase earn",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
