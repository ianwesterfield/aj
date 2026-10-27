# =============================================================================
# AJ Pragmatics API - Application Image
# =============================================================================
# Builds FROM pre-published base image (contains system deps + pip packages + spaCy)
# Only copies application code - rebuilds in ~10 seconds
#
# To rebuild base: see Dockerfile.base
# =============================================================================

ARG BASE_REGISTRY=aj
FROM ${BASE_REGISTRY}/aj-pragmatics-base:latest

WORKDIR /app

# Copy shared module (needed for wait-for-llm)
COPY shared /app/shared

# Make wait-for-llm executable and fix line endings
RUN chmod +x /app/shared/wait-for-llm.sh && \
    sed -i 's/\r$//' /app/shared/wait-for-llm.sh

# Only application code (the fast-changing layer)
COPY pragmatics /app

# Make start script executable and fix line endings
RUN chmod +x /app/start.sh && \
    sed -i 's/\r$//' /app/start.sh

# Move trained models if present
# Priority: 2-class intent_classifier > 4-class distilbert_intent > binary distilbert_memory
RUN if [ -d /app/static/intent_classifier ]; then \
    mv /app/static/intent_classifier /app/distilbert_intent; \
    elif [ -d /app/static/distilbert_intent ]; then \
    mv /app/static/distilbert_intent /app/distilbert_intent; \
    fi && \
    if [ -d /app/static/distilbert_memory ]; then \
    mv /app/static/distilbert_memory /app/distilbert_memory; \
    fi

# Install any missing dependencies
RUN pip install --no-cache-dir httpx>=0.27.0

EXPOSE 8001
CMD ["/app/start.sh"]