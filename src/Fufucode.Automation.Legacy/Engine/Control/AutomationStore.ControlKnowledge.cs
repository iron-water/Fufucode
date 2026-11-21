using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fufucode.Core.Automation;

public sealed partial class AutomationStore
{
    private static void MigrateControlKnowledge(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, """
        CREATE TABLE IF NOT EXISTS control_knowledge_proposals (
          proposal_id TEXT PRIMARY KEY, fact_key TEXT NOT NULL, previous_version INTEGER NOT NULL,
          text TEXT NOT NULL, source TEXT NOT NULL, proposed_by TEXT NOT NULL, status TEXT NOT NULL,
          decided_by TEXT, reason TEXT);
        CREATE TABLE IF NOT EXISTS control_knowledge_facts (
          fact_key TEXT NOT NULL, version INTEGER NOT NULL, text TEXT NOT NULL, source TEXT NOT NULL,
          proposal_id TEXT NOT NULL UNIQUE REFERENCES control_knowledge_proposals(proposal_id),
          published_by TEXT NOT NULL, PRIMARY KEY(fact_key,version));
        CREATE TABLE IF NOT EXISTS control_questions (
          question_id TEXT PRIMARY KEY, from_package TEXT NOT NULL, from_version INTEGER NOT NULL,
          to_package TEXT NOT NULL, to_version INTEGER NOT NULL, text TEXT NOT NULL, asked_by TEXT NOT NULL,
          answer TEXT, answered_by TEXT,
          FOREIGN KEY(from_package,from_version) REFERENCES control_package_versions(package_id,version),
          FOREIGN KEY(to_package,to_version) REFERENCES control_package_versions(package_id,version));
        CREATE TABLE IF NOT EXISTS control_knowledge_bindings (
          package_id TEXT NOT NULL, package_version INTEGER NOT NULL, fact_key TEXT NOT NULL, fact_version INTEGER NOT NULL,
          PRIMARY KEY(package_id,package_version,fact_key),
          FOREIGN KEY(package_id,package_version) REFERENCES control_package_versions(package_id,version),
          FOREIGN KEY(fact_key,fact_version) REFERENCES control_knowledge_facts(fact_key,version));
        CREATE TABLE IF NOT EXISTS control_knowledge_adoptions (
          package_id TEXT NOT NULL, package_version INTEGER NOT NULL, fact_key TEXT NOT NULL, fact_version INTEGER NOT NULL,
          status TEXT NOT NULL, decided_by TEXT,
          PRIMARY KEY(package_id,package_version,fact_key,fact_version),
          FOREIGN KEY(package_id,package_version) REFERENCES control_package_versions(package_id,version),
          FOREIGN KEY(fact_key,fact_version) REFERENCES control_knowledge_facts(fact_key,version));
        """);

    /// <summary>Proposal, immutable published version, answer, revision, event and receipt share one U1 transaction.
    /// Adoption versions a stopped contract; it never launches work or accepts/integrates a delivery.</summary>
    public ControlKnowledgeReceipt ApplyControlKnowledge(ControlKnowledgeCommand command, long nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecidedBy);
        var digest = Sha("knowledge:" + JsonSerializer.Serialize(command));
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var q = ControlSql(c, tx, "SELECT payload_hash,result_json FROM control_receipts WHERE command_id=$id", ("$id", command.CommandId)))
        using (var r = q.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != digest) throw new InvalidOperationException("Knowledge command identity reused with different input.");
                return JsonSerializer.Deserialize<ControlKnowledgeReceipt>(r.GetString(1))!;
            }
        var state = ReadControlSnapshot(c, tx);
        if (state.Revision != command.ExpectedRevision) throw new InvalidOperationException("Refresh the changed control state.");
        var next = checked(state.Revision + 1);
        var id = command.CommandId;
        var status = "pending";
        long? factVersion = null;
        long? packageVersion = null;
        switch (command.Action)
        {
            case ControlKnowledgeAction.Propose:
            {
                RequireKnowledgeText(command.Key, "Knowledge key");
                RequireKnowledgeText(command.Text, "Proposed fact");
                RequireKnowledgeText(command.Source, "Evidence source");
                var previous = CurrentKnowledgeVersion(c, tx, command.Key!);
                if (command.ExpectedKnowledgeVersion != previous) throw new InvalidOperationException("Knowledge version changed before proposal.");
                using var q = ControlSql(c, tx, """
                    INSERT INTO control_knowledge_proposals(proposal_id,fact_key,previous_version,text,source,proposed_by,status)
                    VALUES($id,$key,$version,$text,$source,$actor,'pending')
                    """, ("$id", id), ("$key", command.Key!), ("$version", previous),
                    ("$text", command.Text!), ("$source", command.Source!), ("$actor", command.DecidedBy));
                q.ExecuteNonQuery();
                break;
            }
            case ControlKnowledgeAction.Publish:
            case ControlKnowledgeAction.RejectProposal:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.ProposalId);
                id = command.ProposalId;
                var proposal = ReadControlKnowledge(c, tx).Proposals.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
                if (proposal.Status != "pending") throw new InvalidOperationException("This proposal was already decided.");
                if (command.Action == ControlKnowledgeAction.Publish)
                {
                    var previous = CurrentKnowledgeVersion(c, tx, proposal.Key);
                    if (command.ExpectedKnowledgeVersion != proposal.PreviousVersion || previous != proposal.PreviousVersion)
                        throw new InvalidOperationException("A competing publication changed the fact; propose a fresh version.");
                    factVersion = checked(previous + 1);
                    using var q = ControlSql(c, tx, "INSERT INTO control_knowledge_facts VALUES($key,$version,$text,$source,$proposal,$actor)",
                        ("$key", proposal.Key), ("$version", factVersion.Value), ("$text", proposal.Text),
                        ("$source", proposal.Source), ("$proposal", id), ("$actor", command.DecidedBy));
                    q.ExecuteNonQuery();
                    status = "published_unadopted";
                    CreateKnowledgeAdoptions(c, tx);
                }
                else
                {
                    RequireKnowledgeText(command.Reason, "Rejection reason");
                    status = "rejected";
                }
                using (var q = ControlSql(c, tx, "UPDATE control_knowledge_proposals SET status=$status,decided_by=$actor,reason=$reason WHERE proposal_id=$id",
                    ("$status", status), ("$actor", command.DecidedBy), ("$reason", (object?)command.Reason ?? DBNull.Value), ("$id", id))) q.ExecuteNonQuery();
                break;
            }
            case ControlKnowledgeAction.Ask:
            {
                RequireKnowledgeText(command.Text, "Question");
                ArgumentException.ThrowIfNullOrWhiteSpace(command.FromPackage);
                ArgumentException.ThrowIfNullOrWhiteSpace(command.ToPackage);
                if (command.FromPackage == command.ToPackage) throw new ArgumentException("A directed question must address another package.");
                var from = CurrentControlPackage(c, tx, command.FromPackage);
                var to = CurrentControlPackage(c, tx, command.ToPackage);
                if (from.Superseded || to.Superseded || command.ExpectedFromVersion != from.Version || command.ExpectedToVersion != to.Version)
                    throw new InvalidOperationException("Question names an obsolete package contract.");
                using var q = ControlSql(c, tx, """
                    INSERT INTO control_questions(question_id,from_package,from_version,to_package,to_version,text,asked_by)
                    VALUES($id,$from,$fv,$to,$tv,$text,$actor)
                    """, ("$id", id), ("$from", from.Spec.Id), ("$fv", from.Version), ("$to", to.Spec.Id),
                    ("$tv", to.Version), ("$text", command.Text!), ("$actor", command.DecidedBy));
                q.ExecuteNonQuery(); status = "open";
                break;
            }
            case ControlKnowledgeAction.Answer:
            {
                RequireKnowledgeText(command.Text, "Answer");
                ArgumentException.ThrowIfNullOrWhiteSpace(command.QuestionId);
                id = command.QuestionId;
                var question = ReadControlKnowledge(c, tx).Questions.SingleOrDefault(q => q.Id == id) ?? throw new KeyNotFoundException(id);
                if (!question.ContractCurrent || command.ExpectedFromVersion != question.FromVersion ||
                    command.ExpectedToVersion != question.ToVersion) throw new InvalidOperationException("Question contract changed; ask a new question.");
                if (question.Answer is not null) throw new InvalidOperationException("An existing answer cannot be overwritten.");
                using var q = ControlSql(c, tx, "UPDATE control_questions SET answer=$text,answered_by=$actor WHERE question_id=$id",
                    ("$text", command.Text!), ("$actor", command.DecidedBy), ("$id", id));
                q.ExecuteNonQuery(); status = "human_recorded_answer";
                break;
            }
            case ControlKnowledgeAction.Adopt:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.PackageId);
                RequireKnowledgeText(command.Key, "Knowledge key");
                id = command.PackageId;
                var package = CurrentControlPackage(c, tx, id);
                var latest = CurrentKnowledgeVersion(c, tx, command.Key!);
                if (!package.Approved || command.ExpectedPackageVersion != package.Version || latest == 0 ||
                    command.ExpectedKnowledgeVersion != latest || !(package.Spec.KnowledgeRefs ?? []).Contains(command.Key!, StringComparer.Ordinal))
                    throw new InvalidOperationException("Adoption requires the current approved package and latest declared fact version.");
                using (var q = ControlSql(c, tx, """
                    SELECT COUNT(*) FROM control_knowledge_adoptions WHERE package_id=$id AND package_version=$pv
                    AND fact_key=$key AND fact_version=$fv AND status='pending'
                    """, ("$id", id), ("$pv", package.Version), ("$key", command.Key!), ("$fv", latest)))
                    if (Convert.ToInt64(q.ExecuteScalar()) != 1) throw new InvalidOperationException("This adoption request is absent or obsolete.");
                if (KnowledgeWriterBlock(c, tx, id) is { } blocked)
                    throw new InvalidOperationException("Knowledge adoption is blocked: " + blocked);
                // Reuse the contract-change gate: queued work is abandoned, while active/Unknown writers refuse mutation.
                packageVersion = ReplaceControlPackage(c, tx, package.Spec, package.Version, nowUtc);
                using (var q = ControlSql(c, tx, """
                    INSERT INTO control_knowledge_bindings VALUES($id,$pv,$key,$fv)
                    ON CONFLICT(package_id,package_version,fact_key) DO UPDATE SET fact_version=excluded.fact_version;
                    UPDATE control_packages SET approved_version=version WHERE package_id=$id;
                    INSERT INTO control_approvals VALUES($cmd,$id,$pv,$actor,$mode,$now);
                    UPDATE control_knowledge_adoptions SET status='adopted',decided_by=$actor
                      WHERE package_id=$id AND package_version=$old AND fact_key=$key AND fact_version=$fv;
                    UPDATE control_knowledge_proposals SET status='published' WHERE proposal_id=(
                      SELECT proposal_id FROM control_knowledge_facts WHERE fact_key=$key AND version=$fv);
                    """, ("$id", id), ("$pv", packageVersion.Value), ("$key", command.Key!), ("$fv", latest),
                    ("$cmd", command.CommandId), ("$actor", command.DecidedBy), ("$mode", state.Mode.ToString()),
                    ("$now", nowUtc), ("$old", package.Version))) q.ExecuteNonQuery();
                CreateKnowledgeAdoptions(c, tx);
                RefreshControlIntents(c, tx, state.Mode, next);
                factVersion = latest; status = "adopted";
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        using (var q = ControlSql(c, tx, "UPDATE control_state SET revision=$next WHERE id=1 AND revision=$old",
            ("$next", next), ("$old", state.Revision)))
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent knowledge decision.");
        var receipt = new ControlKnowledgeReceipt(command.CommandId, next, id, status, factVersion) { PackageVersion = packageVersion };
        AppendEvent(c, tx, "control:" + command.CommandId, "control.knowledge." + command.Action,
            "", null, null, null, JsonSerializer.Serialize(new { command, receipt }), nowUtc);
        using (var q = ControlSql(c, tx, "INSERT INTO control_receipts VALUES($id,$hash,$json)", ("$id", command.CommandId),
            ("$hash", digest), ("$json", JsonSerializer.Serialize(receipt)))) q.ExecuteNonQuery();
        tx.Commit(); return receipt;
    }

    private static long CurrentKnowledgeVersion(SqliteConnection c, SqliteTransaction? tx, string key)
    {
        using var q = ControlSql(c, tx, "SELECT COALESCE(MAX(version),0) FROM control_knowledge_facts WHERE fact_key=$key", ("$key", key));
        return Convert.ToInt64(q.ExecuteScalar());
    }

    private static void RequireKnowledgeText(string? text, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, field);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 256 * 1024) throw new ArgumentException(field + " exceeds the byte budget.");
    }

    private static ControlKnowledgeSnapshot ReadControlKnowledge(SqliteConnection c, SqliteTransaction tx)
    {
        var proposals = new List<ControlKnowledgeProposal>();
        using (var q = ControlSql(c, tx, "SELECT proposal_id,fact_key,previous_version,text,source,proposed_by,status,decided_by,reason FROM control_knowledge_proposals ORDER BY proposal_id"))
        using (var r = q.ExecuteReader())
            while (r.Read()) proposals.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        var facts = new List<ControlKnowledgeFact>();
        using (var q = ControlSql(c, tx, "SELECT fact_key,version,text,source,proposal_id,published_by FROM control_knowledge_facts ORDER BY fact_key,version"))
        using (var r = q.ExecuteReader())
            while (r.Read()) facts.Add(new(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        var questions = new List<ControlQuestionState>();
        using (var q = ControlSql(c, tx, """
            SELECT q.question_id,q.from_package,q.from_version,q.to_package,q.to_version,q.text,q.asked_by,q.answer,q.answered_by,
              a.version=q.from_version AND b.version=q.to_version
                AND NOT EXISTS(SELECT 1 FROM control_retired_packages r WHERE r.package_id IN (q.from_package,q.to_package))
            FROM control_questions q JOIN control_packages a ON a.package_id=q.from_package
            JOIN control_packages b ON b.package_id=q.to_package ORDER BY q.question_id
            """))
        using (var r = q.ExecuteReader())
            while (r.Read()) questions.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9) != 0));
        return new(proposals, facts, questions) { Adoptions = ReadKnowledgeAdoptions(c, tx) };
    }
}
