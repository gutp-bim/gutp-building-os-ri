/**
 * Pure validation for the group create/edit form (#143). Create requires a well-formed id + name;
 * edit only validates the name (the id is immutable, matching `PUT /api/Groups/{id}`).
 */
export type GroupFormValidation = { ok: true } | { ok: false; error: string };

/** Group ids are used in URLs and as the resource key — restrict to url-safe alnum + hyphen. */
const ID_PATTERN = /^[A-Za-z0-9-]+$/;

export function validateGroupForm(
  values: { id?: string; name: string },
  opts: { requireId: boolean },
): GroupFormValidation {
  const name = values.name.trim();
  if (opts.requireId) {
    const id = (values.id ?? "").trim();
    if (!id || !name) return { ok: false, error: "ID と名前は必須です" };
    if (!ID_PATTERN.test(id)) {
      return { ok: false, error: "ID は英数字とハイフンのみ使用できます" };
    }
  } else if (!name) {
    return { ok: false, error: "名前は必須です" };
  }
  return { ok: true };
}
