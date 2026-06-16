import type { ReactNode } from "react";
import Link from "next/link";
import "./globals.css";
import { BrandProvider } from "@/components/brand-context";
import { BrandBar } from "@/components/brand-bar";

export const metadata = {
  title: "Quorums",
  description: "Claude-supervised, human-gated Instagram content for DTC brands.",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body>
        <BrandProvider>
          <header className="site-header">
            <div className="site-header__inner">
              <Link href="/" className="brand-mark">
                quorum<span>s</span>
              </Link>
              <BrandBar />
            </div>
          </header>
          {children}
        </BrandProvider>
      </body>
    </html>
  );
}
