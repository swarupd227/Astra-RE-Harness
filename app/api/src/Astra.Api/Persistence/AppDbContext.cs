using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Corpus> Corpora => Set<Corpus>();
    public DbSet<SourceVersion> SourceVersions => Set<SourceVersion>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<Subroutine> Subroutines => Set<Subroutine>();
    public DbSet<Spec> Specs => Set<Spec>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<ClaimReview> ClaimReviews => Set<ClaimReview>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Scaffold> Scaffolds => Set<Scaffold>();
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PlatformConfig> PlatformConfigs => Set<PlatformConfig>();
    public DbSet<GoldenDatasetEntry> GoldenDatasetEntries => Set<GoldenDatasetEntry>();
    public DbSet<GoldenDatasetRun> GoldenDatasetRuns => Set<GoldenDatasetRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PlatformConfig>(b =>
        {
            b.ToTable("platform_configs");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasColumnName("key").HasMaxLength(64).IsRequired();
            b.Property(x => x.ValueJson).HasColumnName("value_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            b.Property(x => x.UpdatedByDisplay).HasColumnName("updated_by_display").HasMaxLength(160);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
            b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(160).IsRequired();
            b.Property(x => x.Persona).HasColumnName("persona").HasMaxLength(32).IsRequired();
            b.Property(x => x.IdpSubject).HasColumnName("idp_subject").HasMaxLength(256).IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Corpus>(b =>
        {
            b.ToTable("corpora");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            b.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(20).IsRequired();
            b.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(2048);
            b.Property(x => x.Branch).HasColumnName("branch").HasMaxLength(255);
            b.Property(x => x.SourceRoot).HasColumnName("source_root").HasMaxLength(1024);
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            b.Property(x => x.FileCount).HasColumnName("file_count");
            b.Property(x => x.TotalLoc).HasColumnName("total_loc");
            b.Property(x => x.OwnerId).HasColumnName("owner_id");
            b.Property(x => x.LatestVersionId).HasColumnName("latest_version_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<SourceVersion>(b =>
        {
            b.ToTable("source_versions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.CorpusId).HasColumnName("corpus_id");
            b.Property(x => x.GitCommitHash).HasColumnName("git_commit_hash").HasMaxLength(64);
            b.Property(x => x.IngestedAt).HasColumnName("ingested_at");
            b.Property(x => x.IngestedBy).HasColumnName("ingested_by");
            b.Property(x => x.FileManifestBlobUri).HasColumnName("file_manifest_blob_uri").HasMaxLength(1024);
            b.HasOne(x => x.Corpus).WithMany(c => c.Versions)
             .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.CorpusId, x.IngestedAt });
        });

        modelBuilder.Entity<SourceFile>(b =>
        {
            b.ToTable("source_files");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SourceVersionId).HasColumnName("source_version_id");
            b.Property(x => x.RelativePath).HasColumnName("relative_path").HasMaxLength(1024).IsRequired();
            b.Property(x => x.FileHash).HasColumnName("file_hash").HasMaxLength(80).IsRequired();
            b.Property(x => x.LineCount).HasColumnName("line_count");
            b.Property(x => x.BlobUri).HasColumnName("blob_uri").HasMaxLength(1024).IsRequired();
            b.HasOne(x => x.SourceVersion).WithMany(v => v.Files)
             .HasForeignKey(x => x.SourceVersionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.SourceVersionId, x.RelativePath }).IsUnique();
        });

        modelBuilder.Entity<Subroutine>(b =>
        {
            b.ToTable("subroutines");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SourceFileId).HasColumnName("source_file_id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
            b.Property(x => x.Signature).HasColumnName("signature").HasMaxLength(2048).IsRequired();
            b.Property(x => x.LineStart).HasColumnName("line_start");
            b.Property(x => x.LineEnd).HasColumnName("line_end");
            b.Property(x => x.CommonBlockRefs).HasColumnName("common_block_refs").HasColumnType("jsonb");
            b.Property(x => x.CalledSubroutines).HasColumnName("called_subroutines").HasColumnType("jsonb");
            b.Property(x => x.IoPatterns).HasColumnName("io_patterns").HasColumnType("jsonb");
            b.Property(x => x.ParsedAstBlobUri).HasColumnName("parsed_ast_blob_uri").HasMaxLength(1024);
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            b.Property(x => x.SourceLanguage).HasColumnName("source_language").HasMaxLength(32).IsRequired();
            b.HasOne(x => x.SourceFile).WithMany(f => f.Subroutines)
             .HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.SourceFileId, x.Name });
            b.HasIndex(x => x.State);
            b.HasIndex(x => x.SourceLanguage);
        });

        modelBuilder.Entity<LlmCall>(b =>
        {
            b.ToTable("llm_calls");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(32).IsRequired();
            b.Property(x => x.Model).HasColumnName("model").HasMaxLength(128).IsRequired();
            b.Property(x => x.PromptTemplateId).HasColumnName("prompt_template_id").HasMaxLength(128).IsRequired();
            b.Property(x => x.PromptTemplateVersion).HasColumnName("prompt_template_version").HasMaxLength(32).IsRequired();
            b.Property(x => x.ProviderConfigVersion).HasColumnName("provider_config_version").HasMaxLength(256).IsRequired();
            b.Property(x => x.InputTokens).HasColumnName("input_tokens");
            b.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            b.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            b.Property(x => x.CostUsd).HasColumnName("cost_usd").HasColumnType("numeric(10,4)");
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            b.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(128);
            b.Property(x => x.CalledBy).HasColumnName("called_by");
            b.Property(x => x.CalledAt).HasColumnName("called_at");
            b.HasIndex(x => x.CalledAt);
            b.HasIndex(x => new { x.Provider, x.CalledAt });
        });

        modelBuilder.Entity<Spec>(b =>
        {
            b.ToTable("specs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SubroutineId).HasColumnName("subroutine_id");
            b.Property(x => x.SourceVersionId).HasColumnName("source_version_id");
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            b.Property(x => x.SpecJson).HasColumnName("spec_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.LlmCallId).HasColumnName("llm_call_id");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.PreviousSpecId).HasColumnName("previous_spec_id");

            b.HasOne(x => x.Subroutine).WithMany()
             .HasForeignKey(x => x.SubroutineId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.LlmCall).WithMany()
             .HasForeignKey(x => x.LlmCallId).OnDelete(DeleteBehavior.SetNull);
            // Self-reference for re-sync lineage. ClientSetNull preserves the
            // audit trail if a prior spec is ever pruned (Phase D archival).
            b.HasOne<Spec>().WithMany()
             .HasForeignKey(x => x.PreviousSpecId).OnDelete(DeleteBehavior.ClientSetNull);

            // Phase C.3: one spec per subroutine still. Re-syncs that carry
            // a spec forward create a NEW subroutine row, so a brand-new
            // (SubroutineId, Spec) pair is what we insert — no constraint
            // conflict with the prior version's spec.
            b.HasIndex(x => x.SubroutineId).IsUnique();
        });

        modelBuilder.Entity<ClaimReview>(b =>
        {
            b.ToTable("claim_reviews");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SpecId).HasColumnName("spec_id");
            b.Property(x => x.ClaimPath).HasColumnName("claim_path").HasMaxLength(512).IsRequired();
            b.Property(x => x.Action).HasColumnName("action").HasMaxLength(32).IsRequired();
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(2000);
            b.Property(x => x.EditedText).HasColumnName("edited_text").HasMaxLength(4000);
            b.Property(x => x.ReviewerId).HasColumnName("reviewer_id");
            b.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            b.HasOne(x => x.Spec).WithMany().HasForeignKey(x => x.SpecId).OnDelete(DeleteBehavior.Cascade);
            // Phase B.3 simplification: one decision per claim_path; replaced
            // (instead of versioned) when the SME re-acts on the same claim.
            b.HasIndex(x => new { x.SpecId, x.ClaimPath }).IsUnique();
        });

        modelBuilder.Entity<Signature>(b =>
        {
            b.ToTable("signatures");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SpecId).HasColumnName("spec_id");
            b.Property(x => x.SignerId).HasColumnName("signer_id");
            b.Property(x => x.SignerDisplay).HasColumnName("signer_display").HasMaxLength(160).IsRequired();
            b.Property(x => x.SignedAt).HasColumnName("signed_at");
            b.Property(x => x.SourceVersionHash).HasColumnName("source_version_hash").HasMaxLength(120).IsRequired();
            b.Property(x => x.SpecCanonicalHash).HasColumnName("spec_canonical_hash").HasMaxLength(120).IsRequired();
            b.Property(x => x.SignatureBytes).HasColumnName("signature_bytes").IsRequired();
            b.Property(x => x.SignatureKeyId).HasColumnName("signature_key_id").HasMaxLength(120).IsRequired();
            b.Property(x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(32).IsRequired();
            b.Property(x => x.SignedBlobUri).HasColumnName("signed_blob_uri").HasMaxLength(1024).IsRequired();
            b.HasOne(x => x.Spec).WithMany().HasForeignKey(x => x.SpecId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.SpecId).IsUnique();  // one signature per spec
        });

        modelBuilder.Entity<SigningKey>(b =>
        {
            b.ToTable("signing_keys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasMaxLength(120);
            b.Property(x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(32).IsRequired();
            b.Property(x => x.PublicKeyPem).HasColumnName("public_key_pem").IsRequired();
            b.Property(x => x.PrivateKeyPem).HasColumnName("private_key_pem").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<Scaffold>(b =>
        {
            b.ToTable("scaffolds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SpecId).HasColumnName("spec_id");
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            b.Property(x => x.LlmCallId).HasColumnName("llm_call_id");
            b.Property(x => x.TargetPlatform).HasColumnName("target_platform").HasMaxLength(32).IsRequired();
            b.Property(x => x.PackageBlobUri).HasColumnName("package_blob_uri").HasMaxLength(1024).IsRequired();
            b.Property(x => x.FileCount).HasColumnName("file_count");
            b.Property(x => x.TotalLines).HasColumnName("total_lines");
            b.Property(x => x.TodoCount).HasColumnName("todo_count");
            b.Property(x => x.GitBranch).HasColumnName("git_branch").HasMaxLength(255);
            b.Property(x => x.GitCommitHash).HasColumnName("git_commit_hash").HasMaxLength(64);
            b.Property(x => x.GitCommitUrl).HasColumnName("git_commit_url").HasMaxLength(1024);
            b.Property(x => x.GeneratedBy).HasColumnName("generated_by");
            b.Property(x => x.GeneratedAt).HasColumnName("generated_at");
            b.HasOne(x => x.Spec).WithMany().HasForeignKey(x => x.SpecId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.LlmCall).WithMany().HasForeignKey(x => x.LlmCallId).OnDelete(DeleteBehavior.SetNull);
            // Phase B.4 simplification: one scaffold per spec.
            b.HasIndex(x => x.SpecId).IsUnique();
        });

        modelBuilder.Entity<ValidationRun>(b =>
        {
            b.ToTable("validation_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ScaffoldId).HasColumnName("scaffold_id");
            b.Property(x => x.SpecId).HasColumnName("spec_id");
            b.Property(x => x.Stage).HasColumnName("stage").HasMaxLength(32).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            b.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(1024).IsRequired();
            b.Property(x => x.LogBlobUri).HasColumnName("log_blob_uri").HasMaxLength(1024);
            b.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(128);
            b.Property(x => x.MetricsJson).HasColumnName("metrics_json").HasColumnType("jsonb");
            b.Property(x => x.StartedBy).HasColumnName("started_by");
            b.Property(x => x.StartedAt).HasColumnName("started_at");
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");

            b.HasOne(x => x.Scaffold).WithMany().HasForeignKey(x => x.ScaffoldId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Spec).WithMany().HasForeignKey(x => x.SpecId).OnDelete(DeleteBehavior.Cascade);

            // Report-card query: WHERE scaffold_id = :id ORDER BY started_at DESC,
            // typically pulling the latest run per stage.
            b.HasIndex(x => new { x.ScaffoldId, x.Stage, x.StartedAt });
            b.HasIndex(x => new { x.SpecId, x.StartedAt });
        });

        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("comments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SpecId).HasColumnName("spec_id");
            b.Property(x => x.ClaimPath).HasColumnName("claim_path").HasMaxLength(512);
            b.Property(x => x.ParentCommentId).HasColumnName("parent_comment_id");
            b.Property(x => x.Body).HasColumnName("body").IsRequired();
            b.Property(x => x.MentionedPersonas).HasColumnName("mentioned_personas").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.AuthorId).HasColumnName("author_id");
            b.Property(x => x.AuthorPersona).HasColumnName("author_persona").HasMaxLength(32).IsRequired();
            b.Property(x => x.AuthorDisplay).HasColumnName("author_display").HasMaxLength(160).IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.EditedAt).HasColumnName("edited_at");
            b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            b.Property(x => x.ResolvedByPersona).HasColumnName("resolved_by_persona").HasMaxLength(32);
            b.Property(x => x.DeletedAt).HasColumnName("deleted_at");

            b.HasOne(x => x.Spec).WithMany().HasForeignKey(x => x.SpecId).OnDelete(DeleteBehavior.Cascade);
            // Self-FK for replies. ClientSetNull keeps the row when its parent is hard-deleted
            // (we soft-delete normally; this is a defensive default).
            b.HasOne<Comment>().WithMany()
             .HasForeignKey(x => x.ParentCommentId).OnDelete(DeleteBehavior.ClientSetNull);

            b.HasIndex(x => new { x.SpecId, x.CreatedAt });
            b.HasIndex(x => new { x.SpecId, x.ClaimPath });
            b.HasIndex(x => x.ParentCommentId);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.RecipientPersona).HasColumnName("recipient_persona").HasMaxLength(32).IsRequired();
            b.Property(x => x.Type).HasColumnName("type").HasMaxLength(64).IsRequired();
            b.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(64).IsRequired();
            b.Property(x => x.TargetId).HasColumnName("target_id");
            b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.ReadAt).HasColumnName("read_at");
            b.Property(x => x.ActorPersona).HasColumnName("actor_persona").HasMaxLength(32);
            b.Property(x => x.ActorDisplay).HasColumnName("actor_display").HasMaxLength(160);

            // Inbox query: WHERE recipient_persona = :p AND read_at IS NULL ORDER BY created_at DESC.
            b.HasIndex(x => new { x.RecipientPersona, x.ReadAt, x.CreatedAt });
            b.HasIndex(x => new { x.TargetType, x.TargetId });
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
            b.Property(x => x.ActorId).HasColumnName("actor_id");
            b.Property(x => x.ActorPersona).HasColumnName("actor_persona").HasMaxLength(32).IsRequired();
            b.Property(x => x.ActorDisplay).HasColumnName("actor_display").HasMaxLength(160).IsRequired();
            b.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(64).IsRequired();
            b.Property(x => x.TargetId).HasColumnName("target_id");
            b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            b.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);

            b.HasIndex(x => new { x.TargetType, x.TargetId, x.OccurredAt });
            b.HasIndex(x => new { x.EventType, x.OccurredAt });
            b.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<GoldenDatasetEntry>(b =>
        {
            b.ToTable("golden_dataset_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EntryId).HasColumnName("entry_id").HasMaxLength(128).IsRequired();
            b.Property(x => x.SchemaId).HasColumnName("schema_id").HasMaxLength(32).IsRequired();
            b.Property(x => x.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
            b.Property(x => x.TrapCategory).HasColumnName("trap_category").HasMaxLength(64).IsRequired();
            b.Property(x => x.Difficulty).HasColumnName("difficulty").HasMaxLength(16).IsRequired();
            b.Property(x => x.SourcePath).HasColumnName("source_path").HasMaxLength(512).IsRequired();
            b.Property(x => x.SourceContent).HasColumnName("source_content").IsRequired();
            b.Property(x => x.SourceLines).HasColumnName("source_lines").HasMaxLength(32).IsRequired();
            b.Property(x => x.ExpectedClaimsJson).HasColumnName("expected_claims").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CanonicalInputsJson).HasColumnName("canonical_inputs").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.Notes).HasColumnName("notes").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(160);

            // EntryId is the natural key from YAML — must be unique so the
            // idempotent seeder can find-or-create by id.
            b.HasIndex(x => x.EntryId).IsUnique();
            // Common filters: list-by-schema and list-by-status from the
            // Admin UI; trap category for "show me all numeric/rounded traps".
            b.HasIndex(x => new { x.SchemaId, x.Status });
            b.HasIndex(x => x.TrapCategory);
        });

        modelBuilder.Entity<GoldenDatasetRun>(b =>
        {
            b.ToTable("golden_dataset_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.EntryId).HasColumnName("entry_id").IsRequired();
            b.Property(x => x.LlmCallId).HasColumnName("llm_call_id");
            b.Property(x => x.PromptId).HasColumnName("prompt_id").HasMaxLength(128).IsRequired();
            b.Property(x => x.PromptVersion).HasColumnName("prompt_version").HasMaxLength(32).IsRequired();
            b.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(128).IsRequired();
            b.Property(x => x.Matched).HasColumnName("matched");
            b.Property(x => x.Total).HasColumnName("total");
            b.Property(x => x.Score).HasColumnName("score");
            b.Property(x => x.DetailJson).HasColumnName("detail").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.StartedAt).HasColumnName("started_at");
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");
            b.Property(x => x.TriggeredBy).HasColumnName("triggered_by").HasMaxLength(160);

            b.HasOne<GoldenDatasetEntry>().WithMany()
             .HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);

            // Score-over-time chart: filter by (prompt, version) ordered
            // by completed_at; per-entry detail: filter by entry_id.
            b.HasIndex(x => new { x.PromptId, x.PromptVersion, x.CompletedAt });
            b.HasIndex(x => new { x.EntryId, x.CompletedAt });
        });
    }
}
