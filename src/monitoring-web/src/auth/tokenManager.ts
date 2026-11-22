/** The slice of keycloak-js the token manager needs (so it can be tested without a browser or Keycloak). */
export interface TokenSource {
  token?: string;
  isTokenExpired(minValiditySeconds?: number): boolean;
  /** Resolves true if a new token was fetched, false if the current one was still valid. Rejects if the session is gone. */
  updateToken(minValiditySeconds: number): Promise<boolean>;
}

/**
 * Hands out access tokens that are valid for at least `minValiditySeconds`. Many requests start at the same time
 * (a page mounts several queries): they all await ONE refresh instead of each hitting the token endpoint, which with
 * rotating refresh tokens would invalidate the others.
 */
export class TokenManager {
  private inflight: Promise<string | null> | null = null;
  refreshCount = 0;

  constructor(
    private readonly source: TokenSource,
    private readonly onSessionExpired: () => void,
    private readonly minValiditySeconds = 30,
  ) {}

  async getToken(): Promise<string | null> {
    if (this.source.token && !this.source.isTokenExpired(this.minValiditySeconds)) return this.source.token;
    return (this.inflight ??= this.refresh().finally(() => { this.inflight = null; }));
  }

  private async refresh(): Promise<string | null> {
    this.refreshCount++;
    try {
      await this.source.updateToken(this.minValiditySeconds);
      return this.source.token ?? null;
    } catch {
      // Refresh token / SSO session is no longer valid: the user has to sign in again.
      this.onSessionExpired();
      return null;
    }
  }
}
