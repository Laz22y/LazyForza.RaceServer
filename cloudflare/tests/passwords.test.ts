import { describe, expect, it } from "vitest";
import { createStoredCredentials, verifyPassword } from "../src/passwords";

describe("Cloudflare password storage", () => {
  it("creates both salted digests and verifies the matching passwords", async () => {
    const credentials = await createStoredCredentials("", "admin-password");

    expect(credentials.player.salt).not.toBe(credentials.admin.salt);
    expect(credentials.player.hash).not.toBe(credentials.admin.hash);
    expect(credentials.player.iterations).toBeGreaterThanOrEqual(100_000);
    expect(await verifyPassword("", credentials.player)).toBe(true);
    expect(await verifyPassword("admin-password", credentials.admin)).toBe(true);
    expect(await verifyPassword("wrong-password", credentials.admin)).toBe(false);
  });

  it("rejects malformed stored digests without throwing", async () => {
    expect(await verifyPassword("anything", {
      salt: "invalid!",
      hash: "also-invalid!",
      iterations: 120_000
    })).toBe(false);
  });
});
