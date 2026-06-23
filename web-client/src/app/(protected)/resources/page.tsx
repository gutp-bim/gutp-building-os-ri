import { Suspense } from "react";
import ResourcesPageComponent from "./page-component";

export default function ResourcesPage() {
  // useSearchParams (in the client component) needs a Suspense boundary under the App Router.
  return (
    <Suspense>
      <ResourcesPageComponent />
    </Suspense>
  );
}
