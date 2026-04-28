from pathlib import Path
import shutil
from app.models import CodePatch, PatchApplyResult


def _find_class_closing_brace(content: str, class_name: str) -> int:
    """Find the position of the closing brace for a given class."""
    class_idx = content.find(f"class {class_name}")
    if class_idx == -1:
        return -1

    # Find the opening brace of the class
    brace_start = content.find("{", class_idx)
    if brace_start == -1:
        return -1

    # Count braces to find the matching close
    depth = 0
    for i in range(brace_start, len(content)):
        if content[i] == "{":
            depth += 1
        elif content[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def apply_patch(patch: CodePatch, repo_root: str) -> PatchApplyResult:
    file_path = Path(repo_root) / patch.file_path

    if not file_path.exists():
        return PatchApplyResult(
            success=False,
            file_path=str(file_path),
            details="Target file does not exist",
        )

    backup_path = str(file_path) + ".bak"
    shutil.copy2(file_path, backup_path)

    content = file_path.read_text(encoding="utf-8")

    try:
        if patch.change_type in {"REPLACE_CONSTANT", "REPLACE_BLOCK"}:
            if not patch.old_content:
                return PatchApplyResult(
                    success=False,
                    file_path=str(file_path),
                    backup_path=backup_path,
                    details=f"old_content is required for {patch.change_type}",
                )

            if patch.old_content not in content:
                return PatchApplyResult(
                    success=False,
                    file_path=str(file_path),
                    backup_path=backup_path,
                    details="old_content not found in target file",
                )

            content = content.replace(patch.old_content, patch.new_content, 1)

        elif patch.change_type in {"INSERT_METHOD", "ADD_GUARD"}:
            # Insert inside target_class, before its closing brace
            insert_pos = _find_class_closing_brace(content, patch.target_class)
            if insert_pos == -1:
                return PatchApplyResult(
                    success=False,
                    file_path=str(file_path),
                    backup_path=backup_path,
                    details=f"Could not find closing brace for class {patch.target_class}",
                )
            content = content[:insert_pos] + "\n" + patch.new_content + "\n" + content[insert_pos:]

        elif patch.change_type == "DELETE_BLOCK":
            if not patch.old_content:
                return PatchApplyResult(
                    success=False,
                    file_path=str(file_path),
                    backup_path=backup_path,
                    details="old_content is required for DELETE_BLOCK",
                )
            if patch.old_content not in content:
                return PatchApplyResult(
                    success=False,
                    file_path=str(file_path),
                    backup_path=backup_path,
                    details="old_content not found in target file",
                )
            content = content.replace(patch.old_content, "", 1)

        else:
            return PatchApplyResult(
                success=False,
                file_path=str(file_path),
                backup_path=backup_path,
                details=f"Unsupported change_type: {patch.change_type}",
            )

        file_path.write_text(content, encoding="utf-8")

        return PatchApplyResult(
            success=True,
            file_path=str(file_path),
            backup_path=backup_path,
            details="Patch applied successfully",
        )

    except Exception as e:
        return PatchApplyResult(
            success=False,
            file_path=str(file_path),
            backup_path=backup_path,
            details=str(e),
        )


def restore_backup(file_path: str, backup_path: str) -> PatchApplyResult:
    try:
        shutil.copy2(backup_path, file_path)
        return PatchApplyResult(
            success=True,
            file_path=file_path,
            backup_path=backup_path,
            details="Backup restored successfully",
        )
    except Exception as e:
        return PatchApplyResult(
            success=False,
            file_path=file_path,
            backup_path=backup_path,
            details=f"Rollback failed: {e}",
        )
