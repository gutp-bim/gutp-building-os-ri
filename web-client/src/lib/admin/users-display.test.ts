import { describe, expect, it } from "vitest";
import {
  EMPTY_USER_FILTER,
  filterUsers,
  isEnabled,
  permissionCount,
  roleLabel,
  statusLabel,
} from "./users-display";
import type { AdminUser } from "./types";

const users: AdminUser[] = [
  { id: "1", displayName: "Alice", email: "alice@x.com", role: "admin", enabled: true, permissions: ["a", "b"] },
  { id: "2", displayName: "Bob", email: "bob@x.com", role: "operator", enabled: false, permissions: [] },
  { id: "3", displayName: "Carol", email: "carol@x.com", role: "viewer" }, // enabled omitted → enabled
];

describe("isEnabled", () => {
  it("treats omitted flag as enabled", () => {
    expect(isEnabled({ id: "x" })).toBe(true);
    expect(isEnabled({ id: "x", enabled: false })).toBe(false);
  });
});

describe("statusLabel / roleLabel / permissionCount", () => {
  it("labels status", () => {
    expect(statusLabel(users[0])).toBe("有効");
    expect(statusLabel(users[1])).toBe("無効");
  });
  it("labels roles in Japanese", () => {
    expect(roleLabel("admin")).toBe("管理者");
    expect(roleLabel("operator")).toBe("運用");
    expect(roleLabel("viewer")).toBe("閲覧");
    expect(roleLabel(null)).toBe("—");
  });
  it("counts permissions defensively", () => {
    expect(permissionCount(users[0])).toBe(2);
    expect(permissionCount(users[2])).toBe(0);
  });
});

describe("filterUsers", () => {
  it("returns all with the empty filter", () => {
    expect(filterUsers(users, EMPTY_USER_FILTER)).toHaveLength(3);
  });
  it("filters by role", () => {
    const r = filterUsers(users, { ...EMPTY_USER_FILTER, role: "admin" });
    expect(r.map((u) => u.id)).toEqual(["1"]);
  });
  it("filters by enabled status", () => {
    expect(filterUsers(users, { ...EMPTY_USER_FILTER, status: "disabled" }).map((u) => u.id)).toEqual(["2"]);
    expect(filterUsers(users, { ...EMPTY_USER_FILTER, status: "enabled" }).map((u) => u.id)).toEqual(["1", "3"]);
  });
  it("filters by free-text query over name/email", () => {
    expect(filterUsers(users, { ...EMPTY_USER_FILTER, query: "carol" }).map((u) => u.id)).toEqual(["3"]);
    expect(filterUsers(users, { ...EMPTY_USER_FILTER, query: "@x.com" })).toHaveLength(3);
  });
  it("combines filters", () => {
    const r = filterUsers(users, { role: "viewer", status: "enabled", query: "car" });
    expect(r.map((u) => u.id)).toEqual(["3"]);
  });
});
