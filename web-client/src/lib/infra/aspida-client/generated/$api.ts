import type { AspidaClient, BasicHeaders } from 'aspida';
import { dataToURLString } from 'aspida';
import type { Methods as Methods_1gmvqd2 } from './api/Auth/check';
import type { Methods as Methods_1pgikh8 } from './api/Auth/me';
import type { Methods as Methods_1ysyt9n } from './api/Groups';
import type { Methods as Methods_qtbldx } from './api/Groups/_id@string';
import type { Methods as Methods_1kz5mun } from './api/Groups/_id@string/resources';
import type { Methods as Methods_l48e78 } from './api/Groups/_id@string/resources/_itemId@string';
import type { Methods as Methods_5fxkl8 } from './api/Groups/_id@string/resources/bulk';
import type { Methods as Methods_g0uczi } from './api/MyResources';
import type { Methods as Methods_st13sh } from './api/MyResources/accessible';
import type { Methods as Methods_1enzbqm } from './api/Permissions/resolve';
import type { Methods as Methods_1bns3zd } from './api/Users';
import type { Methods as Methods_1wjs8in } from './api/Users/_id@string';
import type { Methods as Methods_thf7o9 } from './api/Users/_id@string/attributes';
import type { Methods as Methods_1e6vqb5 } from './api/Users/_id@string/enabled';
import type { Methods as Methods_1mxl4gc } from './api/Users/_id@string/permissions';
import type { Methods as Methods_1ai4eur } from './api/Users/roles';
import type { Methods as Methods_1l2yimm } from './api/admin/audit';
import type { Methods as Methods_1k2arz8 } from './api/admin/gateways';
import type { Methods as Methods_lys6uw } from './api/admin/gateways/_id@string';
import type { Methods as Methods_fzby8s } from './api/admin/gateways/_id@string/resync-pointlist';
import type { Methods as Methods_jyj4sf } from './api/admin/oidc-clients';
import type { Methods as Methods_a29xr5 } from './api/admin/oidc-clients/_id@string';
import type { Methods as Methods_k69hjb } from './api/admin/oidc-clients/_id@string/enabled';
import type { Methods as Methods_z7kt1s } from './api/admin/oidc-clients/_id@string/rotate-secret';
import type { Methods as Methods_26g8bc } from './api/admin/twin/import/apply';
import type { Methods as Methods_2a42vk } from './api/admin/twin/import/preview';
import type { Methods as Methods_l41whu } from './api/admin/twin/query';
import type { Methods as Methods_t3hius } from './api/assistant/chat';
import type { Methods as Methods_o4gmst } from './api/system/config';
import type { Methods as Methods_1y29t8y } from './api/system/settings';
import type { Methods as Methods_196ls2w } from './api/system/settings/_key@string';
import type { Methods as Methods_rdegvd } from './api/system/status';
import type { Methods as Methods_1pg53vd } from './api/telemetry/config';
import type { Methods as Methods_ok90xj } from './buildings';
import type { Methods as Methods_1uitu21 } from './buildings/_buildingDtId@string';
import type { Methods as Methods_a63ipz } from './buildings/_buildingDtId@string/metadata';
import type { Methods as Methods_7yye3n } from './device-details';
import type { Methods as Methods_39vmi5 } from './devices';
import type { Methods as Methods_1o12nnh } from './devices/_deviceDtId@string';
import type { Methods as Methods_1l25dyr } from './devices/_deviceDtId@string/metadata';
import type { Methods as Methods_z40s5x } from './floors';
import type { Methods as Methods_7i9bnr } from './floors/_floorDtId@string';
import type { Methods as Methods_11vktvx } from './floors/_floorDtId@string/metadata';
import type { Methods as Methods_137chuu } from './gateways/_gatewayId@string/pointlist';
import type { Methods as Methods_19trvk3 } from './point-details';
import type { Methods as Methods_1ke14xt } from './point-details/_pointId@string';
import type { Methods as Methods_13o0dsd } from './points';
import type { Methods as Methods_wew8pv } from './points/_pointId@string';
import type { Methods as Methods_ky3pcp } from './points/_pointId@string/control';
import type { Methods as Methods_1tjp38b } from './points/_pointId@string/control-audit';
import type { Methods as Methods_mu5l1t } from './points/_pointId@string/metadata';
import type { Methods as Methods_v8c4mg } from './resources/search';
import type { Methods as Methods_kbl8x1 } from './spaces';
import type { Methods as Methods_zlb8ev } from './spaces/_spaceDtId@string';
import type { Methods as Methods_1pv2qv1 } from './spaces/_spaceDtId@string/metadata';
import type { Methods as Methods_8ytsl6 } from './telemetries/query';
import type { Methods as Methods_1dmkv0v } from './telemetries/query/batch-latest';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/Auth/check';
  const PATH1 = '/api/Auth/me';
  const PATH2 = '/api/Groups';
  const PATH3 = '/resources';
  const PATH4 = '/resources/bulk';
  const PATH5 = '/api/MyResources';
  const PATH6 = '/api/MyResources/accessible';
  const PATH7 = '/api/Permissions/resolve';
  const PATH8 = '/api/Users';
  const PATH9 = '/attributes';
  const PATH10 = '/enabled';
  const PATH11 = '/permissions';
  const PATH12 = '/api/Users/roles';
  const PATH13 = '/api/admin/audit';
  const PATH14 = '/api/admin/gateways';
  const PATH15 = '/resync-pointlist';
  const PATH16 = '/api/admin/oidc-clients';
  const PATH17 = '/rotate-secret';
  const PATH18 = '/api/admin/twin/import/apply';
  const PATH19 = '/api/admin/twin/import/preview';
  const PATH20 = '/api/admin/twin/query';
  const PATH21 = '/api/assistant/chat';
  const PATH22 = '/api/system/config';
  const PATH23 = '/api/system/settings';
  const PATH24 = '/api/system/status';
  const PATH25 = '/api/telemetry/config';
  const PATH26 = '/buildings';
  const PATH27 = '/metadata';
  const PATH28 = '/device-details';
  const PATH29 = '/devices';
  const PATH30 = '/floors';
  const PATH31 = '/gateways';
  const PATH32 = '/pointlist';
  const PATH33 = '/point-details';
  const PATH34 = '/points';
  const PATH35 = '/control';
  const PATH36 = '/control-audit';
  const PATH37 = '/resources/search';
  const PATH38 = '/spaces';
  const PATH39 = '/telemetries/query';
  const PATH40 = '/telemetries/query/batch-latest';
  const GET = 'GET';
  const POST = 'POST';
  const PUT = 'PUT';
  const DELETE = 'DELETE';
  const PATCH = 'PATCH';

  return {
    api: {
      Auth: {
        check: {
          get: (option?: { query?: Methods_1gmvqd2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_1gmvqd2['get']['status']>(prefix, PATH0, GET, option).send(),
          $get: (option?: { query?: Methods_1gmvqd2['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_1gmvqd2['get']['status']>(prefix, PATH0, GET, option).send().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_1gmvqd2['get']['query'] } | undefined) =>
            `${prefix}${PATH0}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        me: {
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_1pgikh8['get']['status']>(prefix, PATH1, GET, option).send(),
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_1pgikh8['get']['status']>(prefix, PATH1, GET, option).send().then(r => r.body),
          $path: () => `${prefix}${PATH1}`,
        },
      },
      Groups: {
        _id: (val2: string) => {
          const prefix2 = `${PATH2}/${val2}`;

          return {
            resources: {
              _itemId: (val4: string) => {
                const prefix4 = `${prefix2}${PATH3}/${val4}`;

                return {
                  delete: (option?: { config?: T | undefined } | undefined) =>
                    fetch<void, BasicHeaders, Methods_l48e78['delete']['status']>(prefix, prefix4, DELETE, option).send(),
                  $delete: (option?: { config?: T | undefined } | undefined) =>
                    fetch<void, BasicHeaders, Methods_l48e78['delete']['status']>(prefix, prefix4, DELETE, option).send().then(r => r.body),
                  $path: () => `${prefix}${prefix4}`,
                };
              },
              bulk: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_5fxkl8['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_5fxkl8['post']['resBody'], BasicHeaders, Methods_5fxkl8['post']['status']>(prefix, `${prefix2}${PATH4}`, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_5fxkl8['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_5fxkl8['post']['resBody'], BasicHeaders, Methods_5fxkl8['post']['status']>(prefix, `${prefix2}${PATH4}`, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix2}${PATH4}`,
              },
              /**
               * @returns Created
               */
              post: (option: { body: Methods_1kz5mun['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1kz5mun['post']['resBody'], BasicHeaders, Methods_1kz5mun['post']['status']>(prefix, `${prefix2}${PATH3}`, POST, option).json(),
              /**
               * @returns Created
               */
              $post: (option: { body: Methods_1kz5mun['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1kz5mun['post']['resBody'], BasicHeaders, Methods_1kz5mun['post']['status']>(prefix, `${prefix2}${PATH3}`, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH3}`,
            },
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_qtbldx['get']['resBody'], BasicHeaders, Methods_qtbldx['get']['status']>(prefix, prefix2, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_qtbldx['get']['resBody'], BasicHeaders, Methods_qtbldx['get']['status']>(prefix, prefix2, GET, option).json().then(r => r.body),
            put: (option: { body: Methods_qtbldx['put']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_qtbldx['put']['status']>(prefix, prefix2, PUT, option).send(),
            $put: (option: { body: Methods_qtbldx['put']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_qtbldx['put']['status']>(prefix, prefix2, PUT, option).send().then(r => r.body),
            delete: (option?: { config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_qtbldx['delete']['status']>(prefix, prefix2, DELETE, option).send(),
            $delete: (option?: { config?: T | undefined } | undefined) =>
              fetch<void, BasicHeaders, Methods_qtbldx['delete']['status']>(prefix, prefix2, DELETE, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix2}`,
          };
        },
        /**
         * @returns OK
         */
        get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1ysyt9n['get']['resBody'], BasicHeaders, Methods_1ysyt9n['get']['status']>(prefix, PATH2, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1ysyt9n['get']['resBody'], BasicHeaders, Methods_1ysyt9n['get']['status']>(prefix, PATH2, GET, option).json().then(r => r.body),
        /**
         * @returns Created
         */
        post: (option: { body: Methods_1ysyt9n['post']['reqBody'], config?: T | undefined }) =>
          fetch<Methods_1ysyt9n['post']['resBody'], BasicHeaders, Methods_1ysyt9n['post']['status']>(prefix, PATH2, POST, option).json(),
        /**
         * @returns Created
         */
        $post: (option: { body: Methods_1ysyt9n['post']['reqBody'], config?: T | undefined }) =>
          fetch<Methods_1ysyt9n['post']['resBody'], BasicHeaders, Methods_1ysyt9n['post']['status']>(prefix, PATH2, POST, option).json().then(r => r.body),
        $path: () => `${prefix}${PATH2}`,
      },
      MyResources: {
        accessible: {
          get: (option?: { query?: Methods_st13sh['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_st13sh['get']['status']>(prefix, PATH6, GET, option).send(),
          $get: (option?: { query?: Methods_st13sh['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<void, BasicHeaders, Methods_st13sh['get']['status']>(prefix, PATH6, GET, option).send().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_st13sh['get']['query'] } | undefined) =>
            `${prefix}${PATH6}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        /**
         * @returns OK
         */
        get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_g0uczi['get']['resBody'], BasicHeaders, Methods_g0uczi['get']['status']>(prefix, PATH5, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_g0uczi['get']['resBody'], BasicHeaders, Methods_g0uczi['get']['status']>(prefix, PATH5, GET, option).json().then(r => r.body),
        $path: () => `${prefix}${PATH5}`,
      },
      Permissions: {
        resolve: {
          /**
           * @returns OK
           */
          post: (option: { body: Methods_1enzbqm['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1enzbqm['post']['resBody'], BasicHeaders, Methods_1enzbqm['post']['status']>(prefix, PATH7, POST, option).json(),
          /**
           * @returns OK
           */
          $post: (option: { body: Methods_1enzbqm['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1enzbqm['post']['resBody'], BasicHeaders, Methods_1enzbqm['post']['status']>(prefix, PATH7, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH7}`,
        },
      },
      Users: {
        _id: (val2: string) => {
          const prefix2 = `${PATH8}/${val2}`;

          return {
            attributes: {
              /**
               * @returns OK
               */
              patch: (option: { body: Methods_thf7o9['patch']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_thf7o9['patch']['resBody'], BasicHeaders, Methods_thf7o9['patch']['status']>(prefix, `${prefix2}${PATH9}`, PATCH, option).json(),
              /**
               * @returns OK
               */
              $patch: (option: { body: Methods_thf7o9['patch']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_thf7o9['patch']['resBody'], BasicHeaders, Methods_thf7o9['patch']['status']>(prefix, `${prefix2}${PATH9}`, PATCH, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH9}`,
            },
            enabled: {
              /**
               * @returns OK
               */
              put: (option: { body: Methods_1e6vqb5['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1e6vqb5['put']['resBody'], BasicHeaders, Methods_1e6vqb5['put']['status']>(prefix, `${prefix2}${PATH10}`, PUT, option).json(),
              /**
               * @returns OK
               */
              $put: (option: { body: Methods_1e6vqb5['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1e6vqb5['put']['resBody'], BasicHeaders, Methods_1e6vqb5['put']['status']>(prefix, `${prefix2}${PATH10}`, PUT, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH10}`,
            },
            permissions: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_1mxl4gc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['post']['resBody'], BasicHeaders, Methods_1mxl4gc['post']['status']>(prefix, `${prefix2}${PATH11}`, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_1mxl4gc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['post']['resBody'], BasicHeaders, Methods_1mxl4gc['post']['status']>(prefix, `${prefix2}${PATH11}`, POST, option).json().then(r => r.body),
              /**
               * @returns OK
               */
              delete: (option: { body: Methods_1mxl4gc['delete']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['delete']['resBody'], BasicHeaders, Methods_1mxl4gc['delete']['status']>(prefix, `${prefix2}${PATH11}`, DELETE, option).json(),
              /**
               * @returns OK
               */
              $delete: (option: { body: Methods_1mxl4gc['delete']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['delete']['resBody'], BasicHeaders, Methods_1mxl4gc['delete']['status']>(prefix, `${prefix2}${PATH11}`, DELETE, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH11}`,
            },
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1wjs8in['get']['resBody'], BasicHeaders, Methods_1wjs8in['get']['status']>(prefix, prefix2, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1wjs8in['get']['resBody'], BasicHeaders, Methods_1wjs8in['get']['status']>(prefix, prefix2, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${prefix2}`,
          };
        },
        roles: {
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1ai4eur['get']['resBody'], BasicHeaders, Methods_1ai4eur['get']['status']>(prefix, PATH12, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1ai4eur['get']['resBody'], BasicHeaders, Methods_1ai4eur['get']['status']>(prefix, PATH12, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH12}`,
        },
        /**
         * @returns OK
         */
        get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1bns3zd['get']['resBody'], BasicHeaders, Methods_1bns3zd['get']['status']>(prefix, PATH8, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1bns3zd['get']['resBody'], BasicHeaders, Methods_1bns3zd['get']['status']>(prefix, PATH8, GET, option).json().then(r => r.body),
        $path: () => `${prefix}${PATH8}`,
      },
      admin: {
        audit: {
          /**
           * @returns OK
           */
          get: (option?: { query?: Methods_1l2yimm['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1l2yimm['get']['resBody'], BasicHeaders, Methods_1l2yimm['get']['status']>(prefix, PATH13, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { query?: Methods_1l2yimm['get']['query'] | undefined, config?: T | undefined } | undefined) =>
            fetch<Methods_1l2yimm['get']['resBody'], BasicHeaders, Methods_1l2yimm['get']['status']>(prefix, PATH13, GET, option).json().then(r => r.body),
          $path: (option?: { method?: 'get' | undefined; query: Methods_1l2yimm['get']['query'] } | undefined) =>
            `${prefix}${PATH13}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
        },
        gateways: {
          _id: (val3: string) => {
            const prefix3 = `${PATH14}/${val3}`;

            return {
              resync_pointlist: {
                post: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_fzby8s['post']['status']>(prefix, `${prefix3}${PATH15}`, POST, option).send(),
                $post: (option?: { config?: T | undefined } | undefined) =>
                  fetch<void, BasicHeaders, Methods_fzby8s['post']['status']>(prefix, `${prefix3}${PATH15}`, POST, option).send().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH15}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_lys6uw['get']['resBody'], BasicHeaders, Methods_lys6uw['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_lys6uw['get']['resBody'], BasicHeaders, Methods_lys6uw['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1k2arz8['get']['resBody'], BasicHeaders, Methods_1k2arz8['get']['status']>(prefix, PATH14, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1k2arz8['get']['resBody'], BasicHeaders, Methods_1k2arz8['get']['status']>(prefix, PATH14, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH14}`,
        },
        oidc_clients: {
          _id: (val3: string) => {
            const prefix3 = `${PATH16}/${val3}`;

            return {
              enabled: {
                /**
                 * @returns OK
                 */
                put: (option: { body: Methods_k69hjb['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_k69hjb['put']['resBody'], BasicHeaders, Methods_k69hjb['put']['status']>(prefix, `${prefix3}${PATH10}`, PUT, option).json(),
                /**
                 * @returns OK
                 */
                $put: (option: { body: Methods_k69hjb['put']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_k69hjb['put']['resBody'], BasicHeaders, Methods_k69hjb['put']['status']>(prefix, `${prefix3}${PATH10}`, PUT, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH10}`,
              },
              rotate_secret: {
                /**
                 * @returns OK
                 */
                post: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_z7kt1s['post']['resBody'], BasicHeaders, Methods_z7kt1s['post']['status']>(prefix, `${prefix3}${PATH17}`, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option?: { config?: T | undefined } | undefined) =>
                  fetch<Methods_z7kt1s['post']['resBody'], BasicHeaders, Methods_z7kt1s['post']['status']>(prefix, `${prefix3}${PATH17}`, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${prefix3}${PATH17}`,
              },
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_a29xr5['get']['resBody'], BasicHeaders, Methods_a29xr5['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_a29xr5['get']['resBody'], BasicHeaders, Methods_a29xr5['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_a29xr5['delete']['status']>(prefix, prefix3, DELETE, option).send(),
              $delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_a29xr5['delete']['status']>(prefix, prefix3, DELETE, option).send().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_jyj4sf['get']['resBody'], BasicHeaders, Methods_jyj4sf['get']['status']>(prefix, PATH16, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_jyj4sf['get']['resBody'], BasicHeaders, Methods_jyj4sf['get']['status']>(prefix, PATH16, GET, option).json().then(r => r.body),
          /**
           * @returns Created
           */
          post: (option: { body: Methods_jyj4sf['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_jyj4sf['post']['resBody'], BasicHeaders, Methods_jyj4sf['post']['status']>(prefix, PATH16, POST, option).json(),
          /**
           * @returns Created
           */
          $post: (option: { body: Methods_jyj4sf['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_jyj4sf['post']['resBody'], BasicHeaders, Methods_jyj4sf['post']['status']>(prefix, PATH16, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH16}`,
        },
        twin: {
          import: {
            apply: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_26g8bc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_26g8bc['post']['resBody'], BasicHeaders, Methods_26g8bc['post']['status']>(prefix, PATH18, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_26g8bc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_26g8bc['post']['resBody'], BasicHeaders, Methods_26g8bc['post']['status']>(prefix, PATH18, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH18}`,
            },
            preview: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_2a42vk['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_2a42vk['post']['resBody'], BasicHeaders, Methods_2a42vk['post']['status']>(prefix, PATH19, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_2a42vk['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_2a42vk['post']['resBody'], BasicHeaders, Methods_2a42vk['post']['status']>(prefix, PATH19, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH19}`,
            },
          },
          query: {
            /**
             * @returns OK
             */
            post: (option: { body: Methods_l41whu['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_l41whu['post']['resBody'], BasicHeaders, Methods_l41whu['post']['status']>(prefix, PATH20, POST, option).json(),
            /**
             * @returns OK
             */
            $post: (option: { body: Methods_l41whu['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_l41whu['post']['resBody'], BasicHeaders, Methods_l41whu['post']['status']>(prefix, PATH20, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH20}`,
          },
        },
      },
      assistant: {
        chat: {
          /**
           * @returns OK
           */
          post: (option: { body: Methods_t3hius['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_t3hius['post']['resBody'], BasicHeaders, Methods_t3hius['post']['status']>(prefix, PATH21, POST, option).json(),
          /**
           * @returns OK
           */
          $post: (option: { body: Methods_t3hius['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_t3hius['post']['resBody'], BasicHeaders, Methods_t3hius['post']['status']>(prefix, PATH21, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH21}`,
        },
      },
      system: {
        config: {
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_o4gmst['get']['resBody'], BasicHeaders, Methods_o4gmst['get']['status']>(prefix, PATH22, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_o4gmst['get']['resBody'], BasicHeaders, Methods_o4gmst['get']['status']>(prefix, PATH22, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH22}`,
        },
        settings: {
          _key: (val3: string) => {
            const prefix3 = `${PATH23}/${val3}`;

            return {
              /**
               * @returns OK
               */
              put: (option: { body: Methods_196ls2w['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_196ls2w['put']['resBody'], BasicHeaders, Methods_196ls2w['put']['status']>(prefix, prefix3, PUT, option).json(),
              /**
               * @returns OK
               */
              $put: (option: { body: Methods_196ls2w['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_196ls2w['put']['resBody'], BasicHeaders, Methods_196ls2w['put']['status']>(prefix, prefix3, PUT, option).json().then(r => r.body),
              delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_196ls2w['delete']['status']>(prefix, prefix3, DELETE, option).send(),
              $delete: (option?: { config?: T | undefined } | undefined) =>
                fetch<void, BasicHeaders, Methods_196ls2w['delete']['status']>(prefix, prefix3, DELETE, option).send().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1y29t8y['get']['resBody'], BasicHeaders, Methods_1y29t8y['get']['status']>(prefix, PATH23, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1y29t8y['get']['resBody'], BasicHeaders, Methods_1y29t8y['get']['status']>(prefix, PATH23, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH23}`,
        },
        status: {
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_rdegvd['get']['resBody'], BasicHeaders, Methods_rdegvd['get']['status']>(prefix, PATH24, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_rdegvd['get']['resBody'], BasicHeaders, Methods_rdegvd['get']['status']>(prefix, PATH24, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH24}`,
        },
      },
      telemetry: {
        config: {
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1pg53vd['get']['resBody'], BasicHeaders, Methods_1pg53vd['get']['status']>(prefix, PATH25, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1pg53vd['get']['resBody'], BasicHeaders, Methods_1pg53vd['get']['status']>(prefix, PATH25, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH25}`,
        },
      },
    },
    buildings: {
      _buildingDtId: (val1: string) => {
        const prefix1 = `${PATH26}/${val1}`;

        return {
          metadata: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_a63ipz['get']['resBody'], BasicHeaders, Methods_a63ipz['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_a63ipz['get']['resBody'], BasicHeaders, Methods_a63ipz['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json().then(r => r.body),
            patch: (option: { body: Methods_a63ipz['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_a63ipz['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send(),
            $patch: (option: { body: Methods_a63ipz['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_a63ipz['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH27}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1uitu21['get']['resBody'], BasicHeaders, Methods_1uitu21['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1uitu21['get']['resBody'], BasicHeaders, Methods_1uitu21['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { config?: T | undefined } | undefined) =>
        fetch<Methods_ok90xj['get']['resBody'], BasicHeaders, Methods_ok90xj['get']['status']>(prefix, PATH26, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { config?: T | undefined } | undefined) =>
        fetch<Methods_ok90xj['get']['resBody'], BasicHeaders, Methods_ok90xj['get']['status']>(prefix, PATH26, GET, option).json().then(r => r.body),
      $path: () => `${prefix}${PATH26}`,
    },
    device_details: {
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_7yye3n['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_7yye3n['get']['resBody'], BasicHeaders, Methods_7yye3n['get']['status']>(prefix, PATH28, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_7yye3n['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_7yye3n['get']['resBody'], BasicHeaders, Methods_7yye3n['get']['status']>(prefix, PATH28, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_7yye3n['get']['query'] } | undefined) =>
        `${prefix}${PATH28}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    devices: {
      _deviceDtId: (val1: string) => {
        const prefix1 = `${PATH29}/${val1}`;

        return {
          metadata: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1l25dyr['get']['resBody'], BasicHeaders, Methods_1l25dyr['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1l25dyr['get']['resBody'], BasicHeaders, Methods_1l25dyr['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json().then(r => r.body),
            patch: (option: { body: Methods_1l25dyr['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_1l25dyr['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send(),
            $patch: (option: { body: Methods_1l25dyr['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_1l25dyr['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH27}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1o12nnh['get']['resBody'], BasicHeaders, Methods_1o12nnh['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1o12nnh['get']['resBody'], BasicHeaders, Methods_1o12nnh['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_39vmi5['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_39vmi5['get']['resBody'], BasicHeaders, Methods_39vmi5['get']['status']>(prefix, PATH29, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_39vmi5['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_39vmi5['get']['resBody'], BasicHeaders, Methods_39vmi5['get']['status']>(prefix, PATH29, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_39vmi5['get']['query'] } | undefined) =>
        `${prefix}${PATH29}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    floors: {
      _floorDtId: (val1: string) => {
        const prefix1 = `${PATH30}/${val1}`;

        return {
          metadata: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_11vktvx['get']['resBody'], BasicHeaders, Methods_11vktvx['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_11vktvx['get']['resBody'], BasicHeaders, Methods_11vktvx['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json().then(r => r.body),
            patch: (option: { body: Methods_11vktvx['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_11vktvx['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send(),
            $patch: (option: { body: Methods_11vktvx['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_11vktvx['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH27}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_7i9bnr['get']['resBody'], BasicHeaders, Methods_7i9bnr['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_7i9bnr['get']['resBody'], BasicHeaders, Methods_7i9bnr['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_z40s5x['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_z40s5x['get']['resBody'], BasicHeaders, Methods_z40s5x['get']['status']>(prefix, PATH30, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_z40s5x['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_z40s5x['get']['resBody'], BasicHeaders, Methods_z40s5x['get']['status']>(prefix, PATH30, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_z40s5x['get']['query'] } | undefined) =>
        `${prefix}${PATH30}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    gateways: {
      _gatewayId: (val1: string) => {
        const prefix1 = `${PATH31}/${val1}`;

        return {
          pointlist: {
            /**
             * The 200 response is BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse for the full list (no `since`, or
             * snapshot evicted) and BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListDiffResponse for a resolvable `?since=`
             * diff. Swagger documents only the full-list shape (Swashbuckle doesn't merge two response types
             * under one status code without a custom schema filter) — treat it as the primary contract.
             * @returns OK
             */
            get: (option?: { query?: Methods_137chuu['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_137chuu['get']['resBody'], BasicHeaders, Methods_137chuu['get']['status']>(prefix, `${prefix1}${PATH32}`, GET, option).json(),
            /**
             * The 200 response is BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse for the full list (no `since`, or
             * snapshot evicted) and BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListDiffResponse for a resolvable `?since=`
             * diff. Swagger documents only the full-list shape (Swashbuckle doesn't merge two response types
             * under one status code without a custom schema filter) — treat it as the primary contract.
             * @returns OK
             */
            $get: (option?: { query?: Methods_137chuu['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_137chuu['get']['resBody'], BasicHeaders, Methods_137chuu['get']['status']>(prefix, `${prefix1}${PATH32}`, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_137chuu['get']['query'] } | undefined) =>
              `${prefix}${prefix1}${PATH32}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        };
      },
    },
    point_details: {
      _pointId: (val1: string) => {
        const prefix1 = `${PATH33}/${val1}`;

        return {
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1ke14xt['get']['resBody'], BasicHeaders, Methods_1ke14xt['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_1ke14xt['get']['resBody'], BasicHeaders, Methods_1ke14xt['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_19trvk3['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_19trvk3['get']['resBody'], BasicHeaders, Methods_19trvk3['get']['status']>(prefix, PATH33, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_19trvk3['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_19trvk3['get']['resBody'], BasicHeaders, Methods_19trvk3['get']['status']>(prefix, PATH33, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_19trvk3['get']['query'] } | undefined) =>
        `${prefix}${PATH33}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    points: {
      _pointId: (val1: string) => {
        const prefix1 = `${PATH34}/${val1}`;

        return {
          control: {
            /**
             * @returns Accepted
             */
            post: (option: { body: Methods_ky3pcp['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_ky3pcp['post']['resBody'], BasicHeaders, Methods_ky3pcp['post']['status']>(prefix, `${prefix1}${PATH35}`, POST, option).json(),
            /**
             * @returns Accepted
             */
            $post: (option: { body: Methods_ky3pcp['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_ky3pcp['post']['resBody'], BasicHeaders, Methods_ky3pcp['post']['status']>(prefix, `${prefix1}${PATH35}`, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH35}`,
          },
          control_audit: {
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_1tjp38b['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1tjp38b['get']['resBody'], BasicHeaders, Methods_1tjp38b['get']['status']>(prefix, `${prefix1}${PATH36}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_1tjp38b['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1tjp38b['get']['resBody'], BasicHeaders, Methods_1tjp38b['get']['status']>(prefix, `${prefix1}${PATH36}`, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1tjp38b['get']['query'] } | undefined) =>
              `${prefix}${prefix1}${PATH36}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          metadata: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_mu5l1t['get']['resBody'], BasicHeaders, Methods_mu5l1t['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_mu5l1t['get']['resBody'], BasicHeaders, Methods_mu5l1t['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json().then(r => r.body),
            patch: (option: { body: Methods_mu5l1t['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_mu5l1t['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send(),
            $patch: (option: { body: Methods_mu5l1t['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_mu5l1t['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH27}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_wew8pv['get']['resBody'], BasicHeaders, Methods_wew8pv['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_wew8pv['get']['resBody'], BasicHeaders, Methods_wew8pv['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_13o0dsd['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_13o0dsd['get']['resBody'], BasicHeaders, Methods_13o0dsd['get']['status']>(prefix, PATH34, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_13o0dsd['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_13o0dsd['get']['resBody'], BasicHeaders, Methods_13o0dsd['get']['status']>(prefix, PATH34, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_13o0dsd['get']['query'] } | undefined) =>
        `${prefix}${PATH34}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    resources: {
      search: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_v8c4mg['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_v8c4mg['get']['resBody'], BasicHeaders, Methods_v8c4mg['get']['status']>(prefix, PATH37, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_v8c4mg['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_v8c4mg['get']['resBody'], BasicHeaders, Methods_v8c4mg['get']['status']>(prefix, PATH37, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_v8c4mg['get']['query'] } | undefined) =>
          `${prefix}${PATH37}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
    },
    spaces: {
      _spaceDtId: (val1: string) => {
        const prefix1 = `${PATH38}/${val1}`;

        return {
          metadata: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1pv2qv1['get']['resBody'], BasicHeaders, Methods_1pv2qv1['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1pv2qv1['get']['resBody'], BasicHeaders, Methods_1pv2qv1['get']['status']>(prefix, `${prefix1}${PATH27}`, GET, option).json().then(r => r.body),
            patch: (option: { body: Methods_1pv2qv1['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_1pv2qv1['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send(),
            $patch: (option: { body: Methods_1pv2qv1['patch']['reqBody'], config?: T | undefined }) =>
              fetch<void, BasicHeaders, Methods_1pv2qv1['patch']['status']>(prefix, `${prefix1}${PATH27}`, PATCH, option).send().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH27}`,
          },
          /**
           * @returns OK
           */
          get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_zlb8ev['get']['resBody'], BasicHeaders, Methods_zlb8ev['get']['status']>(prefix, prefix1, GET, option).json(),
          /**
           * @returns OK
           */
          $get: (option?: { config?: T | undefined } | undefined) =>
            fetch<Methods_zlb8ev['get']['resBody'], BasicHeaders, Methods_zlb8ev['get']['status']>(prefix, prefix1, GET, option).json().then(r => r.body),
          $path: () => `${prefix}${prefix1}`,
        };
      },
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_kbl8x1['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_kbl8x1['get']['resBody'], BasicHeaders, Methods_kbl8x1['get']['status']>(prefix, PATH38, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_kbl8x1['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_kbl8x1['get']['resBody'], BasicHeaders, Methods_kbl8x1['get']['status']>(prefix, PATH38, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_kbl8x1['get']['query'] } | undefined) =>
        `${prefix}${PATH38}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    telemetries: {
      query: {
        batch_latest: {
          /**
           * @returns OK
           */
          post: (option: { body: Methods_1dmkv0v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1dmkv0v['post']['resBody'], BasicHeaders, Methods_1dmkv0v['post']['status']>(prefix, PATH40, POST, option).json(),
          /**
           * @returns OK
           */
          $post: (option: { body: Methods_1dmkv0v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1dmkv0v['post']['resBody'], BasicHeaders, Methods_1dmkv0v['post']['status']>(prefix, PATH40, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH40}`,
        },
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_8ytsl6['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_8ytsl6['get']['resBody'], BasicHeaders, Methods_8ytsl6['get']['status']>(prefix, PATH39, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_8ytsl6['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_8ytsl6['get']['resBody'], BasicHeaders, Methods_8ytsl6['get']['status']>(prefix, PATH39, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_8ytsl6['get']['query'] } | undefined) =>
          `${prefix}${PATH39}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
