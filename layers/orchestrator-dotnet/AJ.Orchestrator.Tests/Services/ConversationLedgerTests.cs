using FluentAssertions;
using AJ.Orchestrator.Domain.Services;
using Xunit;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for ConversationLedger - ported from Python test_cov_session_state.py
/// </summary>
public class ConversationLedgerTests
{
  [Fact]
  public void AddRequest_CreatesEntry()
  {
    var ledger = new ConversationLedger();
    ledger.AddRequest("hello");

    ledger.UserRequests.Should().HaveCount(1);
    ledger.Entries.Should().HaveCount(1);
  }

  [Fact]
  public void AddRequest_LongRequest_Truncates()
  {
    var ledger = new ConversationLedger();
    ledger.AddRequest(new string('x', 200));

    ledger.Entries[0].Summary.Should().Contain("...");
  }

  [Fact]
  public void AddRequest_Over20_TruncatesToLast20()
  {
    var ledger = new ConversationLedger();
    for (int i = 0; i < 25; i++)
    {
      ledger.AddRequest($"req{i}");
    }

    ledger.UserRequests.Should().HaveCount(20);
  }

  [Fact]
  public void AddAction_CreatesActionEntry()
  {
    var ledger = new ConversationLedger();
    ledger.AddAction("execute", "hostname", "ok");

    ledger.Entries.Should().HaveCount(1);
    ledger.Entries[0].EntryType.Should().Be("action");
  }

  [Fact]
  public void AddAction_Over50_TruncatesToLast50()
  {
    var ledger = new ConversationLedger();
    for (int i = 0; i < 55; i++)
    {
      ledger.AddAction("t", "p", "r");
    }

    ledger.Entries.Should().HaveCount(50);
  }

  [Fact]
  public void ExtractValue_StoresValueAndCreatesEntry()
  {
    var ledger = new ConversationLedger();
    ledger.ExtractValue("IP", "10.0.0.1", "execute");

    ledger.ExtractedValues["IP"].Should().Be("10.0.0.1");
    ledger.Entries[0].Summary.Should().Contain("execute");
  }

  [Fact]
  public void ExtractValue_NoSource_OmitsFromSummary()
  {
    var ledger = new ConversationLedger();
    ledger.ExtractValue("port", "8080");

    ledger.Entries[0].Summary.Should().Contain("port");
    ledger.Entries[0].Summary.Should().NotContain("from");
  }

  [Fact]
  public void FormatForPrompt_Empty_ReturnsEmptyString()
  {
    var ledger = new ConversationLedger();

    ledger.FormatForPrompt().Should().BeEmpty();
  }

  [Fact]
  public void FormatForPrompt_Full_ContainsAllData()
  {
    var ledger = new ConversationLedger();
    ledger.ExtractValue("IP", "10.0.0.1");
    ledger.AddRequest("check servers");
    ledger.AddAction("execute", "hostname", "ws1");

    var result = ledger.FormatForPrompt();

    result.Should().Contain("10.0.0.1");
    result.Should().Contain("check servers");
    result.Should().Contain("hostname");
  }

  [Fact]
  public void FormatForPrompt_LongResult_Truncates()
  {
    var ledger = new ConversationLedger();
    ledger.AddAction("execute", "cmd", new string('x', 100));

    var result = ledger.FormatForPrompt();

    result.Should().Contain("...");
  }

  [Fact]
  public void ExtractValue_OverwritesExistingKey()
  {
    var ledger = new ConversationLedger();
    ledger.ExtractValue("IP", "10.0.0.1");
    ledger.ExtractValue("IP", "10.0.0.2");

    ledger.ExtractedValues["IP"].Should().Be("10.0.0.2");
  }

  [Fact]
  public void AddRequest_StoresOriginalInUserRequests()
  {
    var ledger = new ConversationLedger();
    var longRequest = new string('x', 200);
    ledger.AddRequest(longRequest);

    // UserRequests should store the full original
    ledger.UserRequests[0].Should().Be(longRequest);
    // But entry summary should be truncated
    ledger.Entries[0].Summary.Should().HaveLength(103); // 100 + "..."
  }

  [Fact]
  public void FormatForPrompt_OnlyExtractedValues_ShowsQuickReference()
  {
    var ledger = new ConversationLedger();
    ledger.ExtractValue("host", "server1");

    var result = ledger.FormatForPrompt();

    result.Should().Contain("QUICK REFERENCE");
    result.Should().Contain("server1");
  }

  [Fact]
  public void FormatForPrompt_OnlyUserRequests_ShowsRequests()
  {
    var ledger = new ConversationLedger();
    ledger.AddRequest("check disk space");

    var result = ledger.FormatForPrompt();

    result.Should().Contain("USER REQUESTS");
    result.Should().Contain("check disk space");
  }

  [Fact]
  public void FormatForPrompt_OnlyActions_ShowsTimeline()
  {
    var ledger = new ConversationLedger();
    ledger.AddAction("execute", "df -h", "disk info");

    var result = ledger.FormatForPrompt();

    result.Should().Contain("RECENT ACTIONS");
    result.Should().Contain("df -h");
  }

  [Fact]
  public void AddAction_EmptyResult_OmitsResultLine()
  {
    var ledger = new ConversationLedger();
    ledger.AddAction("think", "reasoning", "");

    var result = ledger.FormatForPrompt();

    result.Should().Contain("think: reasoning");
    // Empty results shouldn't have the arrow line
    result.Should().NotContain("→");
  }

  [Fact]
  public void SessionSummary_CanBeSetAndRead()
  {
    var ledger = new ConversationLedger();
    ledger.SessionSummary = "User is checking servers";

    ledger.SessionSummary.Should().Be("User is checking servers");
  }
}
