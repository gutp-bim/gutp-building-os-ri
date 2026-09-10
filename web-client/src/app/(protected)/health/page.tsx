import { Suspense } from "react";
import HealthPageComponent from "./page-component";

export default function HealthPage() {
  // クライアント側の useSearchParams は App Router では Suspense 境界を要求する。
  return (
    <Suspense>
      <HealthPageComponent />
    </Suspense>
  );
}
