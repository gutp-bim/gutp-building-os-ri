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
import type { Methods as Methods_1bns3zd } from './api/Users';
import type { Methods as Methods_1wjs8in } from './api/Users/_id@string';
import type { Methods as Methods_thf7o9 } from './api/Users/_id@string/attributes';
import type { Methods as Methods_1mxl4gc } from './api/Users/_id@string/permissions';
import type { Methods as Methods_ok90xj } from './buildings';
import type { Methods as Methods_1uitu21 } from './buildings/_buildingDtId@string';
import type { Methods as Methods_7yye3n } from './device-details';
import type { Methods as Methods_39vmi5 } from './devices';
import type { Methods as Methods_1o12nnh } from './devices/_deviceDtId@string';
import type { Methods as Methods_z40s5x } from './floors';
import type { Methods as Methods_7i9bnr } from './floors/_floorDtId@string';
import type { Methods as Methods_19trvk3 } from './point-details';
import type { Methods as Methods_1ke14xt } from './point-details/_pointId@string';
import type { Methods as Methods_13o0dsd } from './points';
import type { Methods as Methods_wew8pv } from './points/_pointId@string';
import type { Methods as Methods_ky3pcp } from './points/_pointId@string/control';
import type { Methods as Methods_kbl8x1 } from './spaces';
import type { Methods as Methods_zlb8ev } from './spaces/_spaceDtId@string';
import type { Methods as Methods_7vdlmo } from './telemetries/cold';
import type { Methods as Methods_6bh13z } from './telemetries/cold-multi-point';
import type { Methods as Methods_wglhwh } from './telemetries/hot';
import type { Methods as Methods_134e4fn } from './telemetries/warm';
import type { Methods as Methods_rsearch } from './resources/search';
import type { Methods as Methods_tquery } from './telemetries/query';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/Auth/check';
  const PATH1 = '/api/Auth/me';
  const PATH2 = '/api/Groups';
  const PATH3 = '/resources';
  const PATH4 = '/resources/bulk';
  const PATH5 = '/api/MyResources';
  const PATH6 = '/api/MyResources/accessible';
  const PATH7 = '/api/Users';
  const PATH8 = '/attributes';
  const PATH9 = '/permissions';
  const PATH10 = '/buildings';
  const PATH11 = '/device-details';
  const PATH12 = '/devices';
  const PATH13 = '/floors';
  const PATH14 = '/point-details';
  const PATH15 = '/points';
  const PATH16 = '/control';
  const PATH17 = '/spaces';
  const PATH18 = '/telemetries/cold';
  const PATH19 = '/telemetries/cold-multi-point';
  const PATH20 = '/telemetries/hot';
  const PATH21 = '/telemetries/warm';
  const PATH22 = '/resources/search';
  const PATH23 = '/telemetries/query';
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
      Users: {
        _id: (val2: string) => {
          const prefix2 = `${PATH7}/${val2}`;

          return {
            attributes: {
              /**
               * @returns OK
               */
              patch: (option: { body: Methods_thf7o9['patch']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_thf7o9['patch']['resBody'], BasicHeaders, Methods_thf7o9['patch']['status']>(prefix, `${prefix2}${PATH8}`, PATCH, option).json(),
              /**
               * @returns OK
               */
              $patch: (option: { body: Methods_thf7o9['patch']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_thf7o9['patch']['resBody'], BasicHeaders, Methods_thf7o9['patch']['status']>(prefix, `${prefix2}${PATH8}`, PATCH, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH8}`,
            },
            permissions: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_1mxl4gc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['post']['resBody'], BasicHeaders, Methods_1mxl4gc['post']['status']>(prefix, `${prefix2}${PATH9}`, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_1mxl4gc['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['post']['resBody'], BasicHeaders, Methods_1mxl4gc['post']['status']>(prefix, `${prefix2}${PATH9}`, POST, option).json().then(r => r.body),
              /**
               * @returns OK
               */
              delete: (option: { body: Methods_1mxl4gc['delete']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['delete']['resBody'], BasicHeaders, Methods_1mxl4gc['delete']['status']>(prefix, `${prefix2}${PATH9}`, DELETE, option).json(),
              /**
               * @returns OK
               */
              $delete: (option: { body: Methods_1mxl4gc['delete']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1mxl4gc['delete']['resBody'], BasicHeaders, Methods_1mxl4gc['delete']['status']>(prefix, `${prefix2}${PATH9}`, DELETE, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix2}${PATH9}`,
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
        /**
         * @returns OK
         */
        get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1bns3zd['get']['resBody'], BasicHeaders, Methods_1bns3zd['get']['status']>(prefix, PATH7, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { config?: T | undefined } | undefined) =>
          fetch<Methods_1bns3zd['get']['resBody'], BasicHeaders, Methods_1bns3zd['get']['status']>(prefix, PATH7, GET, option).json().then(r => r.body),
        $path: () => `${prefix}${PATH7}`,
      },
    },
    buildings: {
      _buildingDtId: (val1: string) => {
        const prefix1 = `${PATH10}/${val1}`;

        return {
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
        fetch<Methods_ok90xj['get']['resBody'], BasicHeaders, Methods_ok90xj['get']['status']>(prefix, PATH10, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { config?: T | undefined } | undefined) =>
        fetch<Methods_ok90xj['get']['resBody'], BasicHeaders, Methods_ok90xj['get']['status']>(prefix, PATH10, GET, option).json().then(r => r.body),
      $path: () => `${prefix}${PATH10}`,
    },
    device_details: {
      /**
       * @returns OK
       */
      get: (option?: { query?: Methods_7yye3n['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_7yye3n['get']['resBody'], BasicHeaders, Methods_7yye3n['get']['status']>(prefix, PATH11, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_7yye3n['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_7yye3n['get']['resBody'], BasicHeaders, Methods_7yye3n['get']['status']>(prefix, PATH11, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_7yye3n['get']['query'] } | undefined) =>
        `${prefix}${PATH11}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    devices: {
      _deviceDtId: (val1: string) => {
        const prefix1 = `${PATH12}/${val1}`;

        return {
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
        fetch<Methods_39vmi5['get']['resBody'], BasicHeaders, Methods_39vmi5['get']['status']>(prefix, PATH12, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_39vmi5['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_39vmi5['get']['resBody'], BasicHeaders, Methods_39vmi5['get']['status']>(prefix, PATH12, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_39vmi5['get']['query'] } | undefined) =>
        `${prefix}${PATH12}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    floors: {
      _floorDtId: (val1: string) => {
        const prefix1 = `${PATH13}/${val1}`;

        return {
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
        fetch<Methods_z40s5x['get']['resBody'], BasicHeaders, Methods_z40s5x['get']['status']>(prefix, PATH13, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_z40s5x['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_z40s5x['get']['resBody'], BasicHeaders, Methods_z40s5x['get']['status']>(prefix, PATH13, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_z40s5x['get']['query'] } | undefined) =>
        `${prefix}${PATH13}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    point_details: {
      _pointId: (val1: string) => {
        const prefix1 = `${PATH14}/${val1}`;

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
        fetch<Methods_19trvk3['get']['resBody'], BasicHeaders, Methods_19trvk3['get']['status']>(prefix, PATH14, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_19trvk3['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_19trvk3['get']['resBody'], BasicHeaders, Methods_19trvk3['get']['status']>(prefix, PATH14, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_19trvk3['get']['query'] } | undefined) =>
        `${prefix}${PATH14}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    points: {
      _pointId: (val1: string) => {
        const prefix1 = `${PATH15}/${val1}`;

        return {
          control: {
            /**
             * @param option.body - 制御に関する情報
             * @returns OK
             */
            post: (option: { body: Methods_ky3pcp['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_ky3pcp['post']['resBody'], BasicHeaders, Methods_ky3pcp['post']['status']>(prefix, `${prefix1}${PATH16}`, POST, option).json(),
            /**
             * @param option.body - 制御に関する情報
             * @returns OK
             */
            $post: (option: { body: Methods_ky3pcp['post']['reqBody'], config?: T | undefined }) =>
              fetch<Methods_ky3pcp['post']['resBody'], BasicHeaders, Methods_ky3pcp['post']['status']>(prefix, `${prefix1}${PATH16}`, POST, option).json().then(r => r.body),
            $path: () => `${prefix}${prefix1}${PATH16}`,
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
        fetch<Methods_13o0dsd['get']['resBody'], BasicHeaders, Methods_13o0dsd['get']['status']>(prefix, PATH15, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_13o0dsd['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_13o0dsd['get']['resBody'], BasicHeaders, Methods_13o0dsd['get']['status']>(prefix, PATH15, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_13o0dsd['get']['query'] } | undefined) =>
        `${prefix}${PATH15}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    spaces: {
      _spaceDtId: (val1: string) => {
        const prefix1 = `${PATH17}/${val1}`;

        return {
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
        fetch<Methods_kbl8x1['get']['resBody'], BasicHeaders, Methods_kbl8x1['get']['status']>(prefix, PATH17, GET, option).json(),
      /**
       * @returns OK
       */
      $get: (option?: { query?: Methods_kbl8x1['get']['query'] | undefined, config?: T | undefined } | undefined) =>
        fetch<Methods_kbl8x1['get']['resBody'], BasicHeaders, Methods_kbl8x1['get']['status']>(prefix, PATH17, GET, option).json().then(r => r.body),
      $path: (option?: { method?: 'get' | undefined; query: Methods_kbl8x1['get']['query'] } | undefined) =>
        `${prefix}${PATH17}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
    },
    resources: {
      search: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_rsearch['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_rsearch['get']['resBody'], BasicHeaders, Methods_rsearch['get']['status']>(prefix, PATH22, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_rsearch['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_rsearch['get']['resBody'], BasicHeaders, Methods_rsearch['get']['status']>(prefix, PATH22, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_rsearch['get']['query'] } | undefined) =>
          `${prefix}${PATH22}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
    },
    telemetries: {
      cold: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_7vdlmo['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_7vdlmo['get']['resBody'], BasicHeaders, Methods_7vdlmo['get']['status']>(prefix, PATH18, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_7vdlmo['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_7vdlmo['get']['resBody'], BasicHeaders, Methods_7vdlmo['get']['status']>(prefix, PATH18, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_7vdlmo['get']['query'] } | undefined) =>
          `${prefix}${PATH18}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
      cold_multi_point: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_6bh13z['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_6bh13z['get']['resBody'], BasicHeaders, Methods_6bh13z['get']['status']>(prefix, PATH19, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_6bh13z['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_6bh13z['get']['resBody'], BasicHeaders, Methods_6bh13z['get']['status']>(prefix, PATH19, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_6bh13z['get']['query'] } | undefined) =>
          `${prefix}${PATH19}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
      hot: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_wglhwh['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_wglhwh['get']['resBody'], BasicHeaders, Methods_wglhwh['get']['status']>(prefix, PATH20, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_wglhwh['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_wglhwh['get']['resBody'], BasicHeaders, Methods_wglhwh['get']['status']>(prefix, PATH20, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_wglhwh['get']['query'] } | undefined) =>
          `${prefix}${PATH20}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
      warm: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_134e4fn['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_134e4fn['get']['resBody'], BasicHeaders, Methods_134e4fn['get']['status']>(prefix, PATH21, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_134e4fn['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_134e4fn['get']['resBody'], BasicHeaders, Methods_134e4fn['get']['status']>(prefix, PATH21, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_134e4fn['get']['query'] } | undefined) =>
          `${prefix}${PATH21}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
      query: {
        /**
         * @returns OK
         */
        get: (option?: { query?: Methods_tquery['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_tquery['get']['resBody'], BasicHeaders, Methods_tquery['get']['status']>(prefix, PATH23, GET, option).json(),
        /**
         * @returns OK
         */
        $get: (option?: { query?: Methods_tquery['get']['query'] | undefined, config?: T | undefined } | undefined) =>
          fetch<Methods_tquery['get']['resBody'], BasicHeaders, Methods_tquery['get']['status']>(prefix, PATH23, GET, option).json().then(r => r.body),
        $path: (option?: { method?: 'get' | undefined; query: Methods_tquery['get']['query'] } | undefined) =>
          `${prefix}${PATH23}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
