// storage.js - Simple localStorage wrapper with async API for compatibility

(function() {
  'use strict';

  const storage = {
    /**
     * Load data from localStorage
     * @param {string} key - Storage key
     * @returns {Promise<any>} - Parsed data or null
     */
    async load(key) {
      try {
        const data = localStorage.getItem(key);
        return data ? JSON.parse(data) : null;
      } catch (error) {
        console.error('[storage] Load error:', error);
        return null;
      }
    },

    /**
     * Save data to localStorage
     * @param {string} key - Storage key
     * @param {any} data - Data to save (will be JSON stringified)
     * @returns {Promise<boolean>} - Success status
     */
    async save(key, data) {
      try {
        localStorage.setItem(key, JSON.stringify(data));
        return true;
      } catch (error) {
        console.error('[storage] Save error:', error);
        return false;
      }
    },

    /**
     * Remove data from localStorage
     * @param {string} key - Storage key
     * @returns {Promise<boolean>} - Success status
     */
    async remove(key) {
      try {
        localStorage.removeItem(key);
        return true;
      } catch (error) {
        console.error('[storage] Remove error:', error);
        return false;
      }
    },

    /**
     * Clear all localStorage
     * @returns {Promise<boolean>} - Success status
     */
    async clear() {
      try {
        localStorage.clear();
        return true;
      } catch (error) {
        console.error('[storage] Clear error:', error);
        return false;
      }
    }
  };

  // Expose to window
  window.storage = storage;
})();
