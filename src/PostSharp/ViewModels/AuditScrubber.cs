using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.ViewModels;

/// <summary>
/// Keeps stored audit records free of credentials: masks credential headers and current secret values in every
/// record, writes the result and drops the backups that still hold the old text. It runs at startup and whenever a
/// variable may have become secret.
/// </summary>
/// <param name="audit">The audit list.</param>
/// <param name="trash">The audit recycle bin.</param>
/// <param name="environments">Where secret values come from.</param>
/// <param name="persist">Writes both audit files.</param>
/// <param name="deleteBackups">Deletes the audit backups; returns the ones that could not be deleted.</param>
/// <param name="notice">Where failures are shown.</param>
public sealed class AuditScrubber(
    IReadOnlyCollection<AuditEntry> audit,
    IReadOnlyCollection<AuditEntry> trash,
    IReadOnlyCollection<EnvironmentModel> environments,
    Func<SaveResult> persist,
    Func<IReadOnlyList<string>> deleteBackups,
    DataNoticeViewModel notice
)
{
    private IReadOnlyList<string>? _scrubbedSecrets;

    // The records in memory are already masked after a failed write, so the next pass must write even without changes.
    private bool _writePending;

    public void Scrub()
    {
        var secrets = SecretMasker.CollectSecrets(environments);

        // Records added since the last pass were masked when written, so only a new set of secrets needs a pass.
        if (
            !_writePending
            && _scrubbedSecrets is not null
            && _scrubbedSecrets.SequenceEqual(secrets)
        )
        {
            return;
        }

        var changed = _writePending;
        foreach (var entry in audit.Concat(trash))
        {
            changed |= SecretMasker.MaskAuditEntry(entry, secrets);
        }

        if (changed)
        {
            // The backups still hold the old text; drop them only once the scrubbed files are safely on disk.
            var saved = persist();
            if (saved == SaveResult.Failed)
            {
                _writePending = true;
                notice.Warn(
                    "审计记录已在界面中打码，但写入审计数据库失败，磁盘上的记录仍可能含旧的明文凭据。请检查数据目录是否可写；下次保存环境变量或重启时会再次尝试。"
                );
                return;
            }

            _writePending = false;

            // Blocked: a file unreadable at startup is not written this run (the startup notice says so), and its
            // backup is all that is left of it, so it is kept.
            if (saved == SaveResult.Saved)
            {
                foreach (var backup in deleteBackups())
                {
                    notice.Warn(
                        $"审计备份 {backup} 无法删除，其中仍含打码前的明文凭据。请关闭占用它的程序后手动删除该文件。"
                    );
                }

                Log.Information("已为审计记录中的凭据和机密变量打码");
            }
        }

        _scrubbedSecrets = secrets;
    }
}
