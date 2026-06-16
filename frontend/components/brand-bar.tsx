"use client";

import { useBrand } from "@/components/brand-context";

/** Header control for the active brand id (the X-Brand-Id every API call carries). */
export function BrandBar() {
  const { brandId, setBrandId, ready } = useBrand();

  if (!ready) {
    return null;
  }

  return (
    <label className="brandbar" title="The brand whose runs you are viewing (sent as X-Brand-Id).">
      <span className="eyebrow">Brand</span>
      <input
        value={brandId}
        spellCheck={false}
        placeholder="brand id (GUID)"
        onChange={(e) => setBrandId(e.target.value)}
      />
    </label>
  );
}
