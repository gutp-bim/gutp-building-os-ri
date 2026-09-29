import type { AspidaClient, BasicHeaders } from 'aspida';
import { dataToURLString } from 'aspida';
import type { Methods as Methods_1e7fwhe } from './api/v1/Auth/check';
import type { Methods as Methods_18h0suw } from './api/v1/Auth/me';
import type { Methods as Methods_164y33z } from './api/v1/Groups';
import type { Methods as Methods_1i0b5r5 } from './api/v1/Groups/_id@string';
import type { Methods as Methods_h3yrb7 } from './api/v1/Groups/_id@string/resources';
import type { Methods as Methods_rzqxwo } from './api/v1/Groups/_id@string/resources/_itemId@string';
import type { Methods as Methods_1b4s6tc } from './api/v1/Groups/_id@string/resources/bulk';
import type { Methods as Methods_6cce4a } from './api/v1/MyResources';
import type { Methods as Methods_121r7t1 } from './api/v1/MyResources/accessible';
import type { Methods as Methods_48mjs2 } from './api/v1/Permissions/resolve';
import type { Methods as Methods_2432u5 } from './api/v1/Users';
import type { Methods as Methods_1cqssp7 } from './api/v1/Users/_id@string';
import type { Methods as Methods_3ssqul } from './api/v1/Users/_id@string/attributes';
import type { Methods as Methods_uyw0vx } from './api/v1/Users/_id@string/enabled';
import type { Methods as Methods_cjn24g } from './api/v1/Users/_id@string/permissions';
import type { Methods as Methods_wjz4jb } from './api/v1/Users/roles';
import type { Methods as Methods_4uw3e2 } from './api/v1/admin/audit';
import type { Methods as Methods_2x1c40 } from './api/v1/admin/gateways';
import type { Methods as Methods_irdigc } from './api/v1/admin/gateways/_id@string';
import type { Methods as Methods_1492vsg } from './api/v1/admin/gateways/_id@string/resync-pointlist';
import type { Methods as Methods_5bizur } from './api/v1/admin/oidc-clients';
import type { Methods as Methods_1ulzfdp } from './api/v1/admin/oidc-clients/_id@string';
import type { Methods as Methods_14ng7vn } from './api/v1/admin/oidc-clients/_id@string/enabled';
import type { Methods as Methods_10byft0 } from './api/v1/admin/oidc-clients/_id@string/rotate-secret';
import type { Methods as Methods_1ln53f8 } from './api/v1/admin/twin/import/apply';
import type { Methods as Methods_owal7o } from './api/v1/admin/twin/import/preview';
import type { Methods as Methods_1sogloe } from './api/v1/admin/twin/query';
import type { Methods as Methods_2o9myw } from './api/v1/assistant/chat';
import type { Methods as Methods_ba2c8o } from './api/v1/buildings';
import type { Methods as Methods_1wk42co } from './api/v1/buildings/_buildingDtId@string';
import type { Methods as Methods_1fslbr4 } from './api/v1/buildings/_buildingDtId@string/metadata';
import type { Methods as Methods_1q9lyou } from './api/v1/device-details';
import type { Methods as Methods_bzw7k2 } from './api/v1/devices';
import type { Methods as Methods_nzie9c } from './api/v1/devices/_deviceDtId@string';
import type { Methods as Methods_7ylyhk } from './api/v1/devices/_deviceDtId@string/metadata';
import type { Methods as Methods_1mabrow } from './api/v1/floors';
import type { Methods as Methods_1a201ia } from './api/v1/floors/_floorDtId@string';
import type { Methods as Methods_1royoay } from './api/v1/floors/_floorDtId@string/metadata';
import type { Methods as Methods_ps7i3f } from './api/v1/gateways/_gatewayId@string/pointlist';
import type { Methods as Methods_xkgmf6 } from './api/v1/operations/summary';
import type { Methods as Methods_g3blpg } from './api/v1/point-details';
import type { Methods as Methods_1ji4cx2 } from './api/v1/point-details/_pointId@string';
import type { Methods as Methods_194q44c } from './api/v1/points';
import type { Methods as Methods_1u96wda } from './api/v1/points/_pointId@string';
import type { Methods as Methods_67w8x0 } from './api/v1/points/_pointId@string/control';
import type { Methods as Methods_421wfm } from './api/v1/points/_pointId@string/control-audit';
import type { Methods as Methods_k21t5q } from './api/v1/points/_pointId@string/metadata';
import type { Methods as Methods_gz8asp } from './api/v1/resources/search';
import type { Methods as Methods_ydrv14 } from './api/v1/spaces';
import type { Methods as Methods_402z9e } from './api/v1/spaces/_spaceDtId@string';
import type { Methods as Methods_tpwjen } from './api/v1/spaces/_spaceDtId@string/adjacent-spaces';
import type { Methods as Methods_99cdwq } from './api/v1/spaces/_spaceDtId@string/metadata';
import type { Methods as Methods_902dt } from './api/v1/system/config';
import type { Methods as Methods_4ll7dp } from './api/v1/system/ingress-rejections';
import type { Methods as Methods_4oa9b2 } from './api/v1/system/settings';
import type { Methods as Methods_yms744 } from './api/v1/system/settings/_key@string';
import type { Methods as Methods_3rm085 } from './api/v1/system/status';
import type { Methods as Methods_1kiw6jx } from './api/v1/telemetries/query';
import type { Methods as Methods_ytibni } from './api/v1/telemetries/query/batch-latest';
import type { Methods as Methods_q0ec8l } from './api/v1/telemetry/config';
import type { Methods as Methods_vnm481 } from './api/v1/telemetry/health';
import type { Methods as Methods_qu1y8m } from './api/v1/telemetry/health/summary';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/v1/Auth/check';
  const PATH1 = '/api/v1/Auth/me';
  const PATH2 = '/api/v1/Groups';
  const PATH3 = '/resources';
  const PATH4 = '/resources/bulk';
  const PATH5 = '/api/v1/MyResources';
  const PATH6 = '/api/v1/MyResources/accessible';
  const PATH7 = '/api/v1/Permissions/resolve';
  const PATH8 = '/api/v1/Users';
  const PATH9 = '/attributes';
  const PATH10 = '/enabled';
  const PATH11 = '/permissions';
  const PATH12 = '/api/v1/Users/roles';
  const PATH13 = '/api/v1/admin/audit';
  const PATH14 = '/api/v1/admin/gateways';
  const PATH15 = '/resync-pointlist';
  const PATH16 = '/api/v1/admin/oidc-clients';
  const PATH17 = '/rotate-secret';
  const PATH18 = '/api/v1/admin/twin/import/apply';
  const PATH19 = '/api/v1/admin/twin/import/preview';
  const PATH20 = '/api/v1/admin/twin/query';
  const PATH21 = '/api/v1/assistant/chat';
  const PATH22 = '/api/v1/buildings';
  const PATH23 = '/metadata';
  const PATH24 = '/api/v1/device-details';
  const PATH25 = '/api/v1/devices';
  const PATH26 = '/api/v1/floors';
  const PATH27 = '/api/v1/gateways';
  const PATH28 = '/pointlist';
  const PATH29 = '/api/v1/operations/summary';
  const PATH30 = '/api/v1/point-details';
  const PATH31 = '/api/v1/points';
  const PATH32 = '/control';
  const PATH33 = '/control-audit';
  const PATH34 = '/api/v1/resources/search';
  const PATH35 = '/api/v1/spaces';
  const PATH36 = '/adjacent-spaces';
  const PATH37 = '/api/v1/system/config';
  const PATH38 = '/api/v1/system/ingress-rejections';
  const PATH39 = '/api/v1/system/settings';
  const PATH40 = '/api/v1/system/status';
  const PATH41 = '/api/v1/telemetries/query';
  const PATH42 = '/api/v1/telemetries/query/batch-latest';
  const PATH43 = '/api/v1/telemetry/config';
  const PATH44 = '/api/v1/telemetry/health';
  const PATH45 = '/api/v1/telemetry/health/summary';
  const GET = 'GET';
  const POST = 'POST';
  const PUT = 'PUT';
  const DELETE = 'DELETE';
  const PATCH = 'PATCH';

  return {
    api: {
      v1: {
        Auth: {
          check: {
            get: (option?: { query?: Methods_1e7fwhe['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_1e7fwhe['get']['status']>(prefix, PATH0, GET, option).send(),
            $get: (option?: { query?: Methods_1e7fwhe['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_1e7fwhe['get']['status']>(prefix, PATH0, GET, option).send().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1e7fwhe['get']['query'] } | undefined) =>
              `${prefix}${PATH0}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          me: {
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_18h0suw['get']['status']>(prefix, PATH1, GET, option).send(),
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_18h0suw['get']['status']>(prefix, PATH1, GET, option).send().then(r => r.body),
            $path: () => `${prefix}${PATH1}`,
          },
        },
        Groups: {
          _id: (val3: string) => {
            const prefix3 = `${PATH2}/${val3}`;

            return {
              resources: {
                _itemId: (val5: string) => {
                  const prefix5 = `${prefix3}${PATH3}/${val5}`;

                  return {
                    delete: (option?: { config?: T | undefined } | undefined) =>
                      fetch<void, BasicHeaders, Methods_rzqxwo['delete']['status']>(prefix, prefix5, DELETE, option).send(),
                    $delete: (option?: { config?: T | undefined } | undefined) =>
                      fetch<void, BasicHeaders, Methods_rzqxwo['delete']['status']>(prefix, prefix5, DELETE, option).send().then(r => r.body),
                    $path: () => `${prefix}${prefix5}`,
                  };
                },
                bulk: {
                  /**
                   * @returns OK
                   */
                  post: (option: { body: Methods_1b4s6tc['post']['reqBody'], config?: T | undefined }) =>
                    fetch<Methods_1b4s6tc['post']['resBody'], BasicHeaders, Methods_1b4s6tc['post']['status']>(prefix, `${prefix3}${PATH4}`, POST, option).json(),
                  /**
                   * @returns OK
                   */
                  $post: (option: { body: Methods_1b4s6tc['post']['reqBody'], config?: T | undefined }) =>
                    fetch<Methods_1b4s6tc['post']['resBody'], BasicHeaders, Methods_1b4s6tc['post']['status']>(prefix, `${prefix3}${PATH4}`, POST, option).json().then(r => r.body),
                  $path: () => `${prefix}${prefix3}${PATH4}`,
                },
                /**
                 * @returns Created
                 */
                post: (option: { body: Methods_h3yrb7['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_h3yrb7['post']['resBody'], BasicHeaders, Methods_h3yrb7['post']['status']>(prefix, `${prefix3}${PATH3}`, POST, option).json(),
                /**
                 * @returns Created
                 */
                $post: (option: { body: Methods_h3yrb7['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_h3yrb7['post']['resBody'], BasicHeaders, Methods_h3yrb7['post']['status']>(prefix, `${prefix3}${PATH3}`, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH3}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1i0b5r5['get']['resBody'], BasicHeaders, Methods_1i0b5r5['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1i0b5r5['get']['resBody'], BasicHeaders, Methods_1i0b5r5['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              put: (option: { body: Methods_1i0b5r5['put']['reqBody'], config?: T | undefined }) =>
                fetch<void, BasicHeaders, Methods_1i0b5r5['put']['status']>(prefix, prefix3, PUT, option).send(),
              $put: (option: { body: Methods_1i0b5r5['put']['reqBody'], config?: T | undefined }) =>
                fetch<void, BasicHeaders, Methods_1i0b5r5['put']['status']>(prefix, prefix3, PUT, option).send().then(r => r.body),
              delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_1i0b5r5['delete']['status']>(prefix, prefix3, DELETE, option).send(),
              $delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_1i0b5r5['delete']['status']>(prefix, prefix3, DELETE, option).send().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_164y33z['get']['resBody'], BasicHeaders, Methods_164y33z['get']['status']>(prefix, PATH2, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_164y33z['get']['resBody'], BasicHeaders, Methods_164y33z['get']['status']>(prefix, PATH2, GET, option).json().then(r => r.body),
          /**
           * @returns Created
           */
          post: (option: { body: Methods_164y33z['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_164y33z['post']['resBody'], BasicHeaders, Methods_164y33z['post']['status']>(prefix, PATH2, POST, option).json(),
          /**
           * @returns Created
           */
          $post: (option: { body: Methods_164y33z['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_164y33z['post']['resBody'], BasicHeaders, Methods_164y33z['post']['status']>(prefix, PATH2, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH2}`,
        },
        MyResources: {
          accessible: {
            get: (option?: { query?: Methods_121r7t1['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_121r7t1['get']['status']>(prefix, PATH6, GET, option).send(),
            $get: (option?: { query?: Methods_121r7t1['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_121r7t1['get']['status']>(prefix, PATH6, GET, option).send().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_121r7t1['get']['query'] } | undefined) =>
              `${prefix}${PATH6}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_6cce4a['get']['resBody'], BasicHeaders, Methods_6cce4a['get']['status']>(prefix, PATH5, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_6cce4a['get']['resBody'], BasicHeaders, Methods_6cce4a['get']['status']>(prefix, PATH5, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH5}`,
        },
        Permissions: {
          resolve: {
            /**
             * @returns OK
             */
            post: (option: { body: Methods_48mjs2['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_48mjs2['post']['resBody'], BasicHeaders, Methods_48mjs2['post']['status']>(prefix, PATH7, POST, option).json(),
            /**
             * @returns OK
             */
            $post: (option: { body: Methods_48mjs2['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_48mjs2['post']['resBody'], BasicHeaders, Methods_48mjs2['post']['status']>(prefix, PATH7, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH7}`,
          },
        },
        Users: {
          _id: (val3: string) => {
            const prefix3 = `${PATH8}/${val3}`;

            return {
              attributes: {
                /**
                 * @returns OK
                 */
                patch: (option: { body: Methods_3ssqul['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_3ssqul['patch']['resBody'], BasicHeaders, Methods_3ssqul['patch']['status']>(prefix, `${prefix3}${PATH9}`, PATCH, option).json(),
                /**
                 * @returns OK
                 */
                $patch: (option: { body: Methods_3ssqul['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_3ssqul['patch']['resBody'], BasicHeaders, Methods_3ssqul['patch']['status']>(prefix, `${prefix3}${PATH9}`, PATCH, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH9}`,
              },
              enabled: {
                /**
                 * @returns OK
                 */
                put: (option: { body: Methods_uyw0vx['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_uyw0vx['put']['resBody'], BasicHeaders, Methods_uyw0vx['put']['status']>(prefix, `${prefix3}${PATH10}`, PUT, option).json(),
                /**
                 * @returns OK
                 */
                $put: (option: { body: Methods_uyw0vx['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_uyw0vx['put']['resBody'], BasicHeaders, Methods_uyw0vx['put']['status']>(prefix, `${prefix3}${PATH10}`, PUT, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH10}`,
              },
              permissions: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_cjn24g['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_cjn24g['post']['resBody'], BasicHeaders, Methods_cjn24g['post']['status']>(prefix, `${prefix3}${PATH11}`, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_cjn24g['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_cjn24g['post']['resBody'], BasicHeaders, Methods_cjn24g['post']['status']>(prefix, `${prefix3}${PATH11}`, POST, option).json().then(r => r.body),
                /**
                 * @returns OK
                 */
                delete: (option: { body: Methods_cjn24g['delete']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_cjn24g['delete']['resBody'], BasicHeaders, Methods_cjn24g['delete']['status']>(prefix, `${prefix3}${PATH11}`, DELETE, option).json(),
                /**
                 * @returns OK
                 */
                $delete: (option: { body: Methods_cjn24g['delete']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_cjn24g['delete']['resBody'], BasicHeaders, Methods_cjn24g['delete']['status']>(prefix, `${prefix3}${PATH11}`, DELETE, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH11}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1cqssp7['get']['resBody'], BasicHeaders, Methods_1cqssp7['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1cqssp7['get']['resBody'], BasicHeaders, Methods_1cqssp7['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          roles: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_wjz4jb['get']['resBody'], BasicHeaders, Methods_wjz4jb['get']['status']>(prefix, PATH12, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_wjz4jb['get']['resBody'], BasicHeaders, Methods_wjz4jb['get']['status']>(prefix, PATH12, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH12}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_2432u5['get']['resBody'], BasicHeaders, Methods_2432u5['get']['status']>(prefix, PATH8, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_2432u5['get']['resBody'], BasicHeaders, Methods_2432u5['get']['status']>(prefix, PATH8, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH8}`,
        },
        admin: {
          audit: {
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_4uw3e2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_4uw3e2['get']['resBody'], BasicHeaders, Methods_4uw3e2['get']['status']>(prefix, PATH13, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_4uw3e2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_4uw3e2['get']['resBody'], BasicHeaders, Methods_4uw3e2['get']['status']>(prefix, PATH13, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_4uw3e2['get']['query'] } | undefined) =>
              `${prefix}${PATH13}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          gateways: {
            _id: (val4: string) => {
              const prefix4 = `${PATH14}/${val4}`;

              return {
                resync_pointlist: {
                  post: (option?: { config?: T | undefined } | undefined) =>
                    fetch<void, BasicHeaders, Methods_1492vsg['post']['status']>(prefix, `${prefix4}${PATH15}`, POST, option).send(),
                  $post: (option?: { config?: T | undefined } | undefined) =>
                    fetch<void, BasicHeaders, Methods_1492vsg['post']['status']>(prefix, `${prefix4}${PATH15}`, POST, option).send().then(r => r.body),
                  $path: () => `${prefix}${prefix4}${PATH15}`,
                },
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_irdigc['get']['resBody'], BasicHeaders, Methods_irdigc['get']['status']>(prefix, prefix4, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_irdigc['get']['resBody'], BasicHeaders, Methods_irdigc['get']['status']>(prefix, prefix4, GET, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix4}`,
              };
            },
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_2x1c40['get']['resBody'], BasicHeaders, Methods_2x1c40['get']['status']>(prefix, PATH14, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_2x1c40['get']['resBody'], BasicHeaders, Methods_2x1c40['get']['status']>(prefix, PATH14, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH14}`,
          },
          oidc_clients: {
            _id: (val4: string) => {
              const prefix4 = `${PATH16}/${val4}`;

              return {
                enabled: {
                  /**
                   * @returns OK
                   */
                  put: (option: { body: Methods_14ng7vn['put']['reqBody'], config?: T | undefined }) =>
                    fetch<Methods_14ng7vn['put']['resBody'], BasicHeaders, Methods_14ng7vn['put']['status']>(prefix, `${prefix4}${PATH10}`, PUT, option).json(),
                  /**
                   * @returns OK
                   */
                  $put: (option: { body: Methods_14ng7vn['put']['reqBody'], config?: T | undefined }) =>
                    fetch<Methods_14ng7vn['put']['resBody'], BasicHeaders, Methods_14ng7vn['put']['status']>(prefix, `${prefix4}${PATH10}`, PUT, option).json().then(r => r.body),
                  $path: () => `${prefix}${prefix4}${PATH10}`,
                },
                rotate_secret: {
                  /**
                   * @returns OK
                   */
                  post: (option?: { config?: T | undefined } | undefined) =>
                    fetch<Methods_10byft0['post']['resBody'], BasicHeaders, Methods_10byft0['post']['status']>(prefix, `${prefix4}${PATH17}`, POST, option).json(),
                  /**
                   * @returns OK
                   */
                  $post: (option?: { config?: T | undefined } | undefined) =>
                    fetch<Methods_10byft0['post']['resBody'], BasicHeaders, Methods_10byft0['post']['status']>(prefix, `${prefix4}${PATH17}`, POST, option).json().then(r => r.body),
                  $path: () => `${prefix}${prefix4}${PATH17}`,
                },
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1ulzfdp['get']['resBody'], BasicHeaders, Methods_1ulzfdp['get']['status']>(prefix, prefix4, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1ulzfdp['get']['resBody'], BasicHeaders, Methods_1ulzfdp['get']['status']>(prefix, prefix4, GET, option).json().then(r => r.body),
                delete: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_1ulzfdp['delete']['status']>(prefix, prefix4, DELETE, option).send(),
                $delete: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_1ulzfdp['delete']['status']>(prefix, prefix4, DELETE, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix4}`,
              };
            },
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_5bizur['get']['resBody'], BasicHeaders, Methods_5bizur['get']['status']>(prefix, PATH16, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_5bizur['get']['resBody'], BasicHeaders, Methods_5bizur['get']['status']>(prefix, PATH16, GET, option).json().then(r => r.body),
            /**
             * @returns Created
             */
            post: (option: { body: Methods_5bizur['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_5bizur['post']['resBody'], BasicHeaders, Methods_5bizur['post']['status']>(prefix, PATH16, POST, option).json(),
            /**
             * @returns Created
             */
            $post: (option: { body: Methods_5bizur['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_5bizur['post']['resBody'], BasicHeaders, Methods_5bizur['post']['status']>(prefix, PATH16, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH16}`,
          },
          twin: {
            import: {
              apply: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_1ln53f8['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1ln53f8['post']['resBody'], BasicHeaders, Methods_1ln53f8['post']['status']>(prefix, PATH18, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_1ln53f8['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1ln53f8['post']['resBody'], BasicHeaders, Methods_1ln53f8['post']['status']>(prefix, PATH18, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${PATH18}`,
              },
              preview: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_owal7o['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_owal7o['post']['resBody'], BasicHeaders, Methods_owal7o['post']['status']>(prefix, PATH19, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_owal7o['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_owal7o['post']['resBody'], BasicHeaders, Methods_owal7o['post']['status']>(prefix, PATH19, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${PATH19}`,
              },
            },
            query: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_1sogloe['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1sogloe['post']['resBody'], BasicHeaders, Methods_1sogloe['post']['status']>(prefix, PATH20, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_1sogloe['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1sogloe['post']['resBody'], BasicHeaders, Methods_1sogloe['post']['status']>(prefix, PATH20, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH20}`,
            },
          },
        },
        assistant: {
          chat: {
            /**
             * @returns OK
             */
            post: (option: { body: Methods_2o9myw['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_2o9myw['post']['resBody'], BasicHeaders, Methods_2o9myw['post']['status']>(prefix, PATH21, POST, option).json(),
            /**
             * @returns OK
             */
            $post: (option: { body: Methods_2o9myw['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_2o9myw['post']['resBody'], BasicHeaders, Methods_2o9myw['post']['status']>(prefix, PATH21, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH21}`,
          },
        },
        buildings: {
          _buildingDtId: (val3: string) => {
            const prefix3 = `${PATH22}/${val3}`;

            return {
              metadata: {
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1fslbr4['get']['resBody'], BasicHeaders, Methods_1fslbr4['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1fslbr4['get']['resBody'], BasicHeaders, Methods_1fslbr4['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json().then(r => r.body),
                patch: (option: { body: Methods_1fslbr4['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_1fslbr4['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send(),
                $patch: (option: { body: Methods_1fslbr4['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_1fslbr4['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH23}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1wk42co['get']['resBody'], BasicHeaders, Methods_1wk42co['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1wk42co['get']['resBody'], BasicHeaders, Methods_1wk42co['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_ba2c8o['get']['resBody'], BasicHeaders, Methods_ba2c8o['get']['status']>(prefix, PATH22, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_ba2c8o['get']['resBody'], BasicHeaders, Methods_ba2c8o['get']['status']>(prefix, PATH22, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH22}`,
        },
        device_details: {
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_1q9lyou['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1q9lyou['get']['resBody'], BasicHeaders, Methods_1q9lyou['get']['status']>(prefix, PATH24, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_1q9lyou['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1q9lyou['get']['resBody'], BasicHeaders, Methods_1q9lyou['get']['status']>(prefix, PATH24, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_1q9lyou['get']['query'] } | undefined) =>
            `${prefix}${PATH24}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        devices: {
          _deviceDtId: (val3: string) => {
            const prefix3 = `${PATH25}/${val3}`;

            return {
              metadata: {
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_7ylyhk['get']['resBody'], BasicHeaders, Methods_7ylyhk['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_7ylyhk['get']['resBody'], BasicHeaders, Methods_7ylyhk['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json().then(r => r.body),
                patch: (option: { body: Methods_7ylyhk['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_7ylyhk['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send(),
                $patch: (option: { body: Methods_7ylyhk['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_7ylyhk['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH23}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_nzie9c['get']['resBody'], BasicHeaders, Methods_nzie9c['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_nzie9c['get']['resBody'], BasicHeaders, Methods_nzie9c['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_bzw7k2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_bzw7k2['get']['resBody'], BasicHeaders, Methods_bzw7k2['get']['status']>(prefix, PATH25, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_bzw7k2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_bzw7k2['get']['resBody'], BasicHeaders, Methods_bzw7k2['get']['status']>(prefix, PATH25, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_bzw7k2['get']['query'] } | undefined) =>
            `${prefix}${PATH25}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        floors: {
          _floorDtId: (val3: string) => {
            const prefix3 = `${PATH26}/${val3}`;

            return {
              metadata: {
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1royoay['get']['resBody'], BasicHeaders, Methods_1royoay['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_1royoay['get']['resBody'], BasicHeaders, Methods_1royoay['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json().then(r => r.body),
                patch: (option: { body: Methods_1royoay['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_1royoay['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send(),
                $patch: (option: { body: Methods_1royoay['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_1royoay['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH23}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1a201ia['get']['resBody'], BasicHeaders, Methods_1a201ia['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1a201ia['get']['resBody'], BasicHeaders, Methods_1a201ia['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_1mabrow['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1mabrow['get']['resBody'], BasicHeaders, Methods_1mabrow['get']['status']>(prefix, PATH26, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_1mabrow['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1mabrow['get']['resBody'], BasicHeaders, Methods_1mabrow['get']['status']>(prefix, PATH26, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_1mabrow['get']['query'] } | undefined) =>
            `${prefix}${PATH26}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        gateways: {
          _gatewayId: (val3: string) => {
            const prefix3 = `${PATH27}/${val3}`;

            return {
              pointlist: {
                /**
                 * The 200 response is BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse for the full list (no `since`, or
                 * snapshot evicted) and BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListDiffResponse for a resolvable `?since=`
                 * diff. Swagger documents only the full-list shape (Swashbuckle doesn't merge two response types
                 * under one status code without a custom schema filter) — treat it as the primary contract.
                 * @returns OK
                 */
                get: (option?: { query?: Methods_ps7i3f['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                  fetch<Methods_ps7i3f['get']['resBody'], BasicHeaders, Methods_ps7i3f['get']['status']>(prefix, `${prefix3}${PATH28}`, GET, option).json(),
                /**
                 * The 200 response is BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse for the full list (no `since`, or
                 * snapshot evicted) and BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListDiffResponse for a resolvable `?since=`
                 * diff. Swagger documents only the full-list shape (Swashbuckle doesn't merge two response types
                 * under one status code without a custom schema filter) — treat it as the primary contract.
                 * @returns OK
                 */
                $get: (option?: { query?: Methods_ps7i3f['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                  fetch<Methods_ps7i3f['get']['resBody'], BasicHeaders, Methods_ps7i3f['get']['status']>(prefix, `${prefix3}${PATH28}`, GET, option).json().then(r => r.body),
                $path: (option?: { method?: 'get' | undefined; query: Methods_ps7i3f['get']['query'] } | undefined) =>
                  `${prefix}${prefix3}${PATH28}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
              },
            };
          },
        },
        operations: {
          summary: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_xkgmf6['get']['resBody'], BasicHeaders, Methods_xkgmf6['get']['status']>(prefix, PATH29, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_xkgmf6['get']['resBody'], BasicHeaders, Methods_xkgmf6['get']['status']>(prefix, PATH29, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH29}`,
          },
        },
        point_details: {
          _pointId: (val3: string) => {
            const prefix3 = `${PATH30}/${val3}`;

            return {
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1ji4cx2['get']['resBody'], BasicHeaders, Methods_1ji4cx2['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1ji4cx2['get']['resBody'], BasicHeaders, Methods_1ji4cx2['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_g3blpg['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_g3blpg['get']['resBody'], BasicHeaders, Methods_g3blpg['get']['status']>(prefix, PATH30, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_g3blpg['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_g3blpg['get']['resBody'], BasicHeaders, Methods_g3blpg['get']['status']>(prefix, PATH30, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_g3blpg['get']['query'] } | undefined) =>
            `${prefix}${PATH30}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        points: {
          _pointId: (val3: string) => {
            const prefix3 = `${PATH31}/${val3}`;

            return {
              control: {
                /**
                 * @returns Accepted
                 */
                post: (option: { body: Methods_67w8x0['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_67w8x0['post']['resBody'], BasicHeaders, Methods_67w8x0['post']['status']>(prefix, `${prefix3}${PATH32}`, POST, option).json(),
                /**
                 * @returns Accepted
                 */
                $post: (option: { body: Methods_67w8x0['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_67w8x0['post']['resBody'], BasicHeaders, Methods_67w8x0['post']['status']>(prefix, `${prefix3}${PATH32}`, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH32}`,
              },
              control_audit: {
                /**
                 * @returns OK
                 */
                get: (option?: { query?: Methods_421wfm['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                  fetch<Methods_421wfm['get']['resBody'], BasicHeaders, Methods_421wfm['get']['status']>(prefix, `${prefix3}${PATH33}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { query?: Methods_421wfm['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                  fetch<Methods_421wfm['get']['resBody'], BasicHeaders, Methods_421wfm['get']['status']>(prefix, `${prefix3}${PATH33}`, GET, option).json().then(r => r.body),
                $path: (option?: { method?: 'get' | undefined; query: Methods_421wfm['get']['query'] } | undefined) =>
                  `${prefix}${prefix3}${PATH33}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
              },
              metadata: {
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_k21t5q['get']['resBody'], BasicHeaders, Methods_k21t5q['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_k21t5q['get']['resBody'], BasicHeaders, Methods_k21t5q['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json().then(r => r.body),
                patch: (option: { body: Methods_k21t5q['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_k21t5q['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send(),
                $patch: (option: { body: Methods_k21t5q['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_k21t5q['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH23}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1u96wda['get']['resBody'], BasicHeaders, Methods_1u96wda['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_1u96wda['get']['resBody'], BasicHeaders, Methods_1u96wda['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_194q44c['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_194q44c['get']['resBody'], BasicHeaders, Methods_194q44c['get']['status']>(prefix, PATH31, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_194q44c['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_194q44c['get']['resBody'], BasicHeaders, Methods_194q44c['get']['status']>(prefix, PATH31, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_194q44c['get']['query'] } | undefined) =>
            `${prefix}${PATH31}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        resources: {
          search: {
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_gz8asp['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_gz8asp['get']['resBody'], BasicHeaders, Methods_gz8asp['get']['status']>(prefix, PATH34, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_gz8asp['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_gz8asp['get']['resBody'], BasicHeaders, Methods_gz8asp['get']['status']>(prefix, PATH34, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_gz8asp['get']['query'] } | undefined) =>
              `${prefix}${PATH34}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        },
        spaces: {
          _spaceDtId: (val3: string) => {
            const prefix3 = `${PATH35}/${val3}`;

            return {
              adjacent_spaces: {
                /**
                 * 指定した部屋に隣接する部屋（`sbco:Room`）の一覧。隣接関係は BOT の対称関係
                 * `bot:adjacentZone` に由来し、取り込み時に双方向へ正規化されている。
                 * 読み取り権限のない隣室は結果から除外される。部屋自体が存在しない場合は 404。
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_tpwjen['get']['resBody'], BasicHeaders, Methods_tpwjen['get']['status']>(prefix, `${prefix3}${PATH36}`, GET, option).json(),
                /**
                 * 指定した部屋に隣接する部屋（`sbco:Room`）の一覧。隣接関係は BOT の対称関係
                 * `bot:adjacentZone` に由来し、取り込み時に双方向へ正規化されている。
                 * 読み取り権限のない隣室は結果から除外される。部屋自体が存在しない場合は 404。
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_tpwjen['get']['resBody'], BasicHeaders, Methods_tpwjen['get']['status']>(prefix, `${prefix3}${PATH36}`, GET, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH36}`,
              },
              metadata: {
                /**
                 * @returns OK
                 */
                get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_99cdwq['get']['resBody'], BasicHeaders, Methods_99cdwq['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json(),
                /**
                 * @returns OK
                 */
                $get: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_99cdwq['get']['resBody'], BasicHeaders, Methods_99cdwq['get']['status']>(prefix, `${prefix3}${PATH23}`, GET, option).json().then(r => r.body),
                patch: (option: { body: Methods_99cdwq['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_99cdwq['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send(),
                $patch: (option: { body: Methods_99cdwq['patch']['reqBody'], config?: T | undefined }) =>
                  fetch<void, BasicHeaders, Methods_99cdwq['patch']['status']>(prefix, `${prefix3}${PATH23}`, PATCH, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH23}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_402z9e['get']['resBody'], BasicHeaders, Methods_402z9e['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_402z9e['get']['resBody'], BasicHeaders, Methods_402z9e['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_ydrv14['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_ydrv14['get']['resBody'], BasicHeaders, Methods_ydrv14['get']['status']>(prefix, PATH35, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_ydrv14['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_ydrv14['get']['resBody'], BasicHeaders, Methods_ydrv14['get']['status']>(prefix, PATH35, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_ydrv14['get']['query'] } | undefined) =>
            `${prefix}${PATH35}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        system: {
          config: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_902dt['get']['resBody'], BasicHeaders, Methods_902dt['get']['status']>(prefix, PATH37, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_902dt['get']['resBody'], BasicHeaders, Methods_902dt['get']['status']>(prefix, PATH37, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH37}`,
          },
          ingress_rejections: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_4ll7dp['get']['resBody'], BasicHeaders, Methods_4ll7dp['get']['status']>(prefix, PATH38, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_4ll7dp['get']['resBody'], BasicHeaders, Methods_4ll7dp['get']['status']>(prefix, PATH38, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH38}`,
          },
          settings: {
            _key: (val4: string) => {
              const prefix4 = `${PATH39}/${val4}`;

              return {
                /**
                 * @returns OK
                 */
                put: (option: { body: Methods_yms744['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_yms744['put']['resBody'], BasicHeaders, Methods_yms744['put']['status']>(prefix, prefix4, PUT, option).json(),
                /**
                 * @returns OK
                 */
                $put: (option: { body: Methods_yms744['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_yms744['put']['resBody'], BasicHeaders, Methods_yms744['put']['status']>(prefix, prefix4, PUT, option).json().then(r => r.body),
                delete: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_yms744['delete']['status']>(prefix, prefix4, DELETE, option).send(),
                $delete: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_yms744['delete']['status']>(prefix, prefix4, DELETE, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix4}`,
              };
            },
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_4oa9b2['get']['resBody'], BasicHeaders, Methods_4oa9b2['get']['status']>(prefix, PATH39, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_4oa9b2['get']['resBody'], BasicHeaders, Methods_4oa9b2['get']['status']>(prefix, PATH39, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH39}`,
          },
          status: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_3rm085['get']['resBody'], BasicHeaders, Methods_3rm085['get']['status']>(prefix, PATH40, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_3rm085['get']['resBody'], BasicHeaders, Methods_3rm085['get']['status']>(prefix, PATH40, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH40}`,
          },
        },
        telemetries: {
          query: {
            batch_latest: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_ytibni['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_ytibni['post']['resBody'], BasicHeaders, Methods_ytibni['post']['status']>(prefix, PATH42, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_ytibni['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_ytibni['post']['resBody'], BasicHeaders, Methods_ytibni['post']['status']>(prefix, PATH42, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH42}`,
            },
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_1kiw6jx['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1kiw6jx['get']['resBody'], BasicHeaders, Methods_1kiw6jx['get']['status']>(prefix, PATH41, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_1kiw6jx['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1kiw6jx['get']['resBody'], BasicHeaders, Methods_1kiw6jx['get']['status']>(prefix, PATH41, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1kiw6jx['get']['query'] } | undefined) =>
              `${prefix}${PATH41}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        },
        telemetry: {
          config: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_q0ec8l['get']['resBody'], BasicHeaders, Methods_q0ec8l['get']['status']>(prefix, PATH43, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_q0ec8l['get']['resBody'], BasicHeaders, Methods_q0ec8l['get']['status']>(prefix, PATH43, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH43}`,
          },
          health: {
            summary: {
              /**
               * @returns OK
               */
              get: (option?: { query?: Methods_qu1y8m['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                fetch<Methods_qu1y8m['get']['resBody'], BasicHeaders, Methods_qu1y8m['get']['status']>(prefix, PATH45, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { query?: Methods_qu1y8m['get']['query'] | undefined, config?: T | undefined } | undefined) =>
                fetch<Methods_qu1y8m['get']['resBody'], BasicHeaders, Methods_qu1y8m['get']['status']>(prefix, PATH45, GET, option).json().then(r => r.body),
              $path: (option?: { method?: 'get' | undefined; query: Methods_qu1y8m['get']['query'] } | undefined) =>
                `${prefix}${PATH45}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
            },
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_vnm481['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_vnm481['get']['resBody'], BasicHeaders, Methods_vnm481['get']['status']>(prefix, PATH44, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_vnm481['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_vnm481['get']['resBody'], BasicHeaders, Methods_vnm481['get']['status']>(prefix, PATH44, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_vnm481['get']['query'] } | undefined) =>
              `${prefix}${PATH44}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        },
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
