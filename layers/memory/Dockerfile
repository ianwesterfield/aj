# =============================================================================
# AJ Memory API - Application Image
# =============================================================================
# Builds FROM pre-published base image (contains system deps + pip packages)
# Only copies application code - rebuilds in ~10 seconds
#
# To rebuild base: see Dockerfile.base
# =============================================================================

ARG BASE_REGISTRY=aj
FROM ${BASE_REGISTRY}/aj-memory-base:latest

WORKDIR /app

# Only application code (the fast-changing layer)
COPY memory/ .

EXPOSE 8000
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]