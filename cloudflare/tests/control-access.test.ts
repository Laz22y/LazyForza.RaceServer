import { describe, expect, it } from "vitest";
import { controlRoleAllows, requiredControlPermission } from "../src/control-access";
import {
  authenticateControlAccount,
  controlAccountSummaries,
  createControlAccount,
  createStoredCredentials,
  deleteControlAccount,
  normalizeStoredCredentials,
  updateControlAccount
} from "../src/passwords";

describe("Race Control access", () => {
  it("migrates the legacy password and supports multiple accounts per role", async () => {
    const initial = await createStoredCredentials("room-password", "owner-password");
    const legacy = normalizeStoredCredentials({ player: initial.player, admin: initial.admin });
    expect(legacy.changed).toBe(true);
    expect(await authenticateControlAccount(legacy.credentials, "owner-password")).toMatchObject({
      name: "初始超管", role: "superAdmin"
    });

    const admin = await createControlAccount(legacy.credentials, {
      name: "赛事管理员", role: "administrator", password: "admin-password"
    });
    const stewardOne = await createControlAccount(admin.credentials, {
      name: "一号裁判", role: "steward", password: "steward-password-1"
    });
    const stewardTwo = await createControlAccount(stewardOne.credentials, {
      name: "二号裁判", role: "steward", password: "steward-password-2"
    });
    expect(controlAccountSummaries(stewardTwo.credentials)).toHaveLength(4);
    expect(await authenticateControlAccount(stewardTwo.credentials, "steward-password-2"))
      .toMatchObject({ name: "二号裁判", role: "steward" });
    await expect(createControlAccount(stewardTwo.credentials, {
      name: "重复密码", role: "steward", password: "steward-password-1"
    })).rejects.toThrow("不同密码");
    await expect(createControlAccount(stewardTwo.credentials, {
      name: "房间密码", role: "steward", password: "room-password"
    })).rejects.toThrow("房间密码");

    const updated = await updateControlAccount(
      stewardTwo.credentials,
      admin.account.id,
      { name: "赛事管理员 A", role: "administrator", password: "admin-password-new" });
    expect(await authenticateControlAccount(updated.credentials, "admin-password")).toBeNull();
    expect(await authenticateControlAccount(updated.credentials, "admin-password-new"))
      .toMatchObject({ name: "赛事管理员 A" });

    const owner = controlAccountSummaries(updated.credentials).find(account => account.role === "superAdmin")!;
    expect(() => deleteControlAccount(updated.credentials, owner.id)).toThrow("至少需要保留一个超管");
    expect(deleteControlAccount(updated.credentials, stewardOne.account.id).deleted).toBe(true);
  });

  it("maps roles and admin routes to the required permissions", () => {
    expect(controlRoleAllows("superAdmin", "manageControlAccounts")).toBe(true);
    expect(controlRoleAllows("administrator", "manageControlAccounts")).toBe(false);
    expect(controlRoleAllows("administrator", "manageRace")).toBe(true);
    expect(controlRoleAllows("steward", "adjudicate")).toBe(true);
    expect(controlRoleAllows("steward", "manageRace")).toBe(false);
    expect(requiredControlPermission("GET", "/api/admin/state")).toBe("view");
    expect(requiredControlPermission("POST", "/api/admin/penalty/update")).toBe("adjudicate");
    expect(requiredControlPermission("POST", "/api/admin/flag")).toBe("manageRace");
    expect(requiredControlPermission("POST", "/api/admin/participant")).toBe("manageRace");
    expect(requiredControlPermission("DELETE", "/api/admin/control-accounts/123"))
      .toBe("manageControlAccounts");
  });
});
