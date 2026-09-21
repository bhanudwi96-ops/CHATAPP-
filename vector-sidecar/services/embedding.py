"""
services/embedding.py

Generates text embeddings using the Gemini text-embedding-004 API.
Output vectors are L2-normalised so that inner-product == cosine similarity,
which is what FAISS IndexFlatIP computes.
"""

import os
import logging
import httpx
import numpy as np
from typing import List, Optional
from dotenv import load_dotenv

load_dotenv()

logger = logging.getLogger(__name__)

# Gemini embedding endpoint — model name is configurable via env
EMBEDDING_MODEL = os.getenv("EMBEDDING_MODEL", "gemini-embedding-001")
GEMINI_API_KEY  = os.getenv("GEMINI_API_KEY", "")
VECTOR_DIM      = 768   # Gemini embedding output dimension (matches FAISS IndexFlatIP)

_ENDPOINT_TEMPLATE = (
    "https://generativelanguage.googleapis.com/v1beta/models/"
    "{model}:embedContent?key={key}"
)


_client: Optional[httpx.AsyncClient] = None


async def get_http_client() -> httpx.AsyncClient:
    """Get or create the shared persistent httpx.AsyncClient with connection pooling."""
    global _client
    if _client is None or _client.is_closed:
        limits = httpx.Limits(max_connections=50, max_keepalive_connections=20, keepalive_expiry=60.0)
        _client = httpx.AsyncClient(limits=limits, timeout=10.0)
    return _client


async def close_http_client():
    """Gracefully close the shared httpx.AsyncClient upon application shutdown."""
    global _client
    if _client is not None and not _client.is_closed:
        await _client.aclose()
        _client = None


def _l2_normalise(vec: List[float]) -> List[float]:
    """L2-normalise a vector so FAISS inner-product == cosine similarity."""
    arr  = np.array(vec, dtype=np.float32)
    norm = np.linalg.norm(arr)
    if norm == 0:
        return vec
    return (arr / norm).tolist()


async def embed_text(text: str) -> Optional[List[float]]:
    """
    Embed a single text string using Gemini embedContent API.
    Returns L2-normalised float list, or None on failure.
    Reuses the persistent httpx.AsyncClient connection pool.
    """
    if not text or not text.strip():
        return None

    api_key = GEMINI_API_KEY or os.getenv("GEMINI_API_KEY", "")
    if not api_key or "YOUR_GEMINI" in api_key.upper():
        logger.warning("GEMINI_API_KEY not set — cannot embed text.")
        return None

    model = EMBEDDING_MODEL
    endpoint = _ENDPOINT_TEMPLATE.format(model=model, key=api_key)
    # Truncate at 2000 chars (Gemini embedContent limit)
    truncated = text[:2000] if len(text) > 2000 else text

    payload = {
        "model": f"models/{model}",
        "content": {
            "parts": [{"text": truncated}]
        },
        "outputDimensionality": VECTOR_DIM
    }

    try:
        client = await get_http_client()
        resp = await client.post(endpoint, json=payload)
        if not resp.is_success:
            logger.warning(
                "Gemini embedding API returned %s for text '%s...'",
                resp.status_code, text[:60]
            )
            return None

        data   = resp.json()
        values = data.get("embedding", {}).get("values")
        if not values:
            logger.warning("Unexpected embedding response structure: %s", data)
            return None

        return _l2_normalise(values)

    except httpx.TimeoutException:
        logger.warning("Gemini embedding API timed out for text '%s...'", text[:60])
        return None
    except Exception as exc:
        logger.error("Embedding API call failed: %s", exc)
        return None


async def embed_batch(texts: List[str]) -> List[Optional[List[float]]]:
    """
    Embed a list of texts, calling embed_text for each.
    Returns a list of the same length; None entries indicate failures.
    """
    results = []
    for i, text in enumerate(texts):
        vec = await embed_text(text)
        results.append(vec)
        if (i + 1) % 10 == 0:
            logger.info("Embedded %d / %d entries...", i + 1, len(texts))
    return results
