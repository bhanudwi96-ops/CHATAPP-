"""
main.py — Python FastAPI FAISS Vector Sidecar

Endpoints:
  GET  /health           → readiness + indexed entry count
  POST /search           → semantic search via FAISS
  POST /index            → upsert one KB entry (embed + index)
  DELETE /index/{id}     → logically remove a KB entry from the index

Startup sequence:
  1. Try to load FAISS index from disk (fast, sub-second)
  2. If no persisted index found: read all rows from KnowledgeBase SQL table,
     batch-embed via Gemini text-embedding-004, build index, save to disk
  3. Start serving requests
"""

import asyncio
import logging
import os
from contextlib import asynccontextmanager
from typing import AsyncGenerator

from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse

# Load .env before importing services (they read env vars at module level)
load_dotenv()

from models import (  # noqa: E402
    HealthResponse,
    IndexRequest,
    SearchRequest,
    SidecarSearchResponse,
    SearchResult,
)
from services.embedding import embed_text, embed_batch, get_http_client, close_http_client
from services.faiss_index import FaissIndex
from services.db_loader import load_all_kb_entries

# ── Logging ────────────────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("vector-sidecar")

# ── Global index instance ──────────────────────────────────────────────────────
INDEX_PATH  = os.getenv("INDEX_PATH",  "index.faiss")
ID_MAP_PATH = os.getenv("ID_MAP_PATH", "id_map.json")
_index      = FaissIndex(INDEX_PATH, ID_MAP_PATH)


# ── Startup / Shutdown ─────────────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncGenerator[None, None]:
    """
    FastAPI lifespan: runs startup before first request, shutdown after last.
    """
    logger.info("=== Vector Sidecar starting up ===")
    await get_http_client()  # Initialize shared persistent connection pool

    # Step 1: try loading from disk
    loaded = _index.load()

    if not loaded:
        # Step 2: build from DB
        logger.info("No persisted index found — building from KnowledgeBase table...")
        try:
            entries = load_all_kb_entries()
            if entries:
                texts = [f"{e['topic']}: {e['content']}" for e in entries]
                logger.info("Embedding %d KB entries (this may take a moment)...", len(entries))
                vectors = await embed_batch(texts)

                indexed = 0
                for entry, vec in zip(entries, vectors):
                    if vec is None:
                        logger.warning("Skipping entry id=%d — embedding failed.", entry["id"])
                        continue
                    _index.add(entry["id"], vec, entry["topic"], entry["content"])
                    indexed += 1

                logger.info("Indexed %d / %d KB entries.", indexed, len(entries))
                _index.save()
            else:
                logger.warning("KnowledgeBase table is empty — index will be empty until entries are added.")
        except RuntimeError as exc:
            logger.error("DB load failed at startup: %s", exc)
            logger.warning("Sidecar will serve with an empty index until /index is called.")
    else:
        logger.info("Index ready from disk: %d live entries.", _index.count)

    logger.info("=== Vector Sidecar ready on port %s ===", os.getenv("SIDECAR_PORT", "8001"))
    yield  # serve requests

    # Shutdown: save latest index state and close connection pool
    logger.info("=== Vector Sidecar shutting down — saving index & closing connection pool... ===")
    _index.save()
    await close_http_client()


# ── App ────────────────────────────────────────────────────────────────────────
app = FastAPI(
    title="ChatApp Vector Sidecar",
    description="FAISS-backed semantic search for the KnowledgeBase",
    version="1.0.0",
    lifespan=lifespan,
)


# ── Endpoints ──────────────────────────────────────────────────────────────────

@app.get("/health", response_model=HealthResponse, tags=["meta"])
async def health() -> HealthResponse:
    """Readiness check — returns 200 with entry count when ready."""
    return HealthResponse(
        status="healthy",
        indexed_count=_index.count,
        index_loaded_from_disk=_index.loaded_from_disk,
    )


@app.post("/search", response_model=SidecarSearchResponse, tags=["search"])
async def search(req: SearchRequest) -> SidecarSearchResponse:
    """
    Semantic search over the FAISS index.
    Embeds the query via Gemini, then returns top-K results above threshold.
    """
    logger.info("Search request: '%s' (top_k=%d, threshold=%.2f)",
                req.query[:80], req.top_k, req.threshold)

    query_vec = await embed_text(req.query)
    if query_vec is None:
        logger.warning("Failed to embed query '%s' — returning empty results.", req.query[:60])
        return SidecarSearchResponse(results=[])

    hits = _index.search(query_vec, top_k=req.top_k, threshold=req.threshold)

    results = [
        SearchResult(id=db_id, topic=topic, content=content, score=score)
        for db_id, topic, content, score in hits
    ]
    logger.info("Search returned %d results for '%s'.", len(results), req.query[:60])
    return SidecarSearchResponse(results=results)


@app.post("/index", status_code=200, tags=["index"])
async def upsert_entry(req: IndexRequest) -> dict:
    """
    Embed and index (or re-index) a single KB entry.
    Safe to call multiple times for the same id — it will replace the old vector.
    """
    combined = f"{req.topic}: {req.content}"
    vec = await embed_text(combined)
    if vec is None:
        raise HTTPException(status_code=502, detail="Gemini embedding API unavailable.")

    _index.add(req.id, vec, req.topic, req.content)
    _index.save()

    logger.info("Indexed KB entry id=%d (topic='%s').", req.id, req.topic[:60])
    return {"status": "indexed", "id": req.id, "indexed_count": _index.count}


@app.delete("/index/{entry_id}", status_code=200, tags=["index"])
async def delete_entry(entry_id: int) -> dict:
    """Remove a KB entry from the FAISS index (logical delete)."""
    removed = _index.delete(entry_id)
    if not removed:
        raise HTTPException(status_code=404, detail=f"Entry id={entry_id} not found in index.")

    _index.save()
    logger.info("Removed KB entry id=%d from index.", entry_id)
    return {"status": "removed", "id": entry_id, "indexed_count": _index.count}


# ── Entrypoint (for direct `python main.py` runs) ─────────────────────────────
if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("SIDECAR_PORT", "8001"))
    uvicorn.run("main:app", host="127.0.0.1", port=port, reload=False)
