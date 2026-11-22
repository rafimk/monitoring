import { TokenManager, type TokenSource } from "./tokenManager";

function source(over: Partial<TokenSource> & { expired?: boolean } = {}) {
  const s = {
    token: "t1" as string | undefined,
    expired: over.expired ?? false,
    updates: 0,
    isTokenExpired(_min?: number) { return this.expired; },
    updateToken: over.updateToken ?? (async function (this: any) { s.updates++; await new Promise((r) => setTimeout(r, 20)); s.token = "t2"; s.expired = false; return true; }),
  };
  return s;
}

describe("TokenManager", () => {
  it("returns the current token without contacting Keycloak while it is still valid", async () => {
    const s = source();
    const m = new TokenManager(s as any, () => {});
    expect(await m.getToken()).toBe("t1");
    expect(s.updates).toBe(0);
  });

  it("refreshes before handing out a token that is about to expire", async () => {
    const s = source({ expired: true });
    const m = new TokenManager(s as any, () => {});
    expect(await m.getToken()).toBe("t2");
    expect(s.updates).toBe(1);
  });

  it("shares ONE refresh between simultaneous requests (no refresh race)", async () => {
    const s = source({ expired: true });
    const m = new TokenManager(s as any, () => {});
    const tokens = await Promise.all(Array.from({ length: 12 }, () => m.getToken()));
    expect(tokens.every((t) => t === "t2")).toBe(true);
    expect(s.updates).toBe(1);
    expect(m.refreshCount).toBe(1);
  });

  it("allows a new refresh once the previous one finished and the token expires again", async () => {
    const s = source({ expired: true });
    const m = new TokenManager(s as any, () => {});
    await m.getToken();
    s.expired = true;
    await m.getToken();
    expect(s.updates).toBe(2);
  });

  it("reports an expired session once, returns null to every waiting caller, and recovers on the next call", async () => {
    const onExpired = vi.fn();
    let fail = true;
    const s = source({
      expired: true,
      updateToken: async () => { if (fail) throw new Error("refresh token expired"); s.token = "t3"; s.expired = false; return true; },
    });
    const m = new TokenManager(s as any, onExpired);
    const results = await Promise.all([m.getToken(), m.getToken(), m.getToken()]);
    expect(results).toEqual([null, null, null]);
    expect(onExpired).toHaveBeenCalledTimes(1);

    fail = false; // e.g. the user signed in again
    expect(await m.getToken()).toBe("t3");
  });
});
