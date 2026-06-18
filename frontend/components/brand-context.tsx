"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

// Which tenant the dashboard is viewing. There is no auth system in the MVP, so the brand id is a
// client-held selection (persisted to localStorage, optionally seeded from an env default) sent to the
// API as the X-Brand-Id header. It is an identifier, not a secret.

const STORAGE_KEY = "quorums.brandId";
const fallback = process.env.NEXT_PUBLIC_DEMO_BRAND_ID ?? "";

interface BrandContextValue {
  brandId: string;
  setBrandId: (id: string) => void;
  ready: boolean;
}

const BrandContext = createContext<BrandContextValue | null>(null);

export function BrandProvider({ children }: { children: ReactNode }) {
  const [brandId, setState] = useState("");
  const [ready, setReady] = useState(false);

  useEffect(() => {
    setState(window.localStorage.getItem(STORAGE_KEY) ?? fallback);
    setReady(true);
  }, []);

  const setBrandId = useCallback((id: string) => {
    const trimmed = id.trim();
    window.localStorage.setItem(STORAGE_KEY, trimmed);
    setState(trimmed);
  }, []);

  return (
    <BrandContext.Provider value={{ brandId, setBrandId, ready }}>
      {children}
    </BrandContext.Provider>
  );
}

export function useBrand(): BrandContextValue {
  const value = useContext(BrandContext);
  if (!value) {
    throw new Error("useBrand must be used within a BrandProvider");
  }
  return value;
}
