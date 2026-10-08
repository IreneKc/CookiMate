import os
import secrets
from pathlib import Path

from dotenv import load_dotenv
from fastapi import HTTPException, Security
from fastapi.security import APIKeyHeader

load_dotenv(Path(__file__).with_name(".env"), encoding="utf-8-sig")

INTERNAL_API_KEY = os.environ["INTERNAL_API_KEY"]

api_key_header = APIKeyHeader(name="X-Api-Key", auto_error=False)


def require_api_key(api_key: str | None = Security(api_key_header)) -> None:
    if not api_key or not secrets.compare_digest(api_key, INTERNAL_API_KEY):
        raise HTTPException(status_code=401, detail="Invalid or missing API key")
