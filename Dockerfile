# ═══════════════════════════════════════════════════════════════════════════════
# Stage 1 — Build
# ═══════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore dependencies first (better layer caching)
COPY ImageDownloader.csproj .
RUN dotnet restore

# Copy source and publish
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Install Playwright CLI and download Chromium browser binaries.
# Done in the BUILD stage so binaries are cached as a layer and copied
# to the runtime stage — no network access needed at container start.
RUN dotnet tool install --global Microsoft.Playwright.CLI \
    && /root/.dotnet/tools/playwright install chromium --with-deps

# ═══════════════════════════════════════════════════════════════════════════════
# Stage 2 — Runtime
# ═══════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# ── Chromium system dependencies ──────────────────────────────────────────────
# Full list from `playwright install-deps chromium` on Debian Bookworm.
# libasound2t64 is the correct package name on Debian 12+ / Ubuntu 22.04+
# (was renamed from libasound2).
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core Chromium libs
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libatspi2.0-0 \
    # Audio (try both names for broad distro compat)
    && (apt-get install -y --no-install-recommends libasound2t64 2>/dev/null \
        || apt-get install -y --no-install-recommends libasound2 2>/dev/null \
        || true) \
    # Font, SSL, X11 stubs
    && apt-get install -y --no-install-recommends \
    fonts-liberation \
    fonts-noto-color-emoji \
    ca-certificates \
    wget \
    libx11-6 \
    libx11-xcb1 \
    libxcb1 \
    libxext6 \
    # Shared memory / tmpfs helpers for headless Chrome
    && rm -rf /var/lib/apt/lists/*

# ── Copy published app ────────────────────────────────────────────────────────
COPY --from=build /app/publish .

# ── Copy Playwright Chromium binaries from build stage ───────────────────────
COPY --from=build /root/.cache/ms-playwright /root/.cache/ms-playwright

# ── Copy Playwright CLI (optional — only needed if install runs at runtime) ──
COPY --from=build /root/.dotnet/tools /root/.dotnet/tools
ENV PATH="$PATH:/root/.dotnet/tools"

# ── Environment ───────────────────────────────────────────────────────────────
ENV ASPNETCORE_ENVIRONMENT=Production \
    PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    # Increase shared memory for headless Chrome on platforms with small /dev/shm
    CHROMIUM_FLAGS="--disable-dev-shm-usage" \
    # Raise default HttpClient limits for busy crawl jobs
    DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=true

EXPOSE 8080

# Graceful shutdown — give running jobs time to finish
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "ImageDownloader.dll"]
