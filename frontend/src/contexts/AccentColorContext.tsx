import React, { createContext, useContext, useState, useEffect, useCallback, ReactNode } from 'react';
import { GetAccentColor, SetAccentColor } from '../../wailsjs/go/app/App';

interface AccentColorContextType {
  accentColor: string;
  setAccentColor: (color: string) => Promise<void>;
}

const AccentColorContext = createContext<AccentColorContextType | undefined>(undefined);

// Helper to convert hex to RGB values
const hexToRgb = (hex: string): { r: number; g: number; b: number } | null => {
  const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
  return result
    ? {
        r: parseInt(result[1], 16),
        g: parseInt(result[2], 16),
        b: parseInt(result[3], 16),
      }
    : null;
};

// Update CSS variables on the document root
const updateCssVariables = (color: string) => {
  const root = document.documentElement;
  const rgb = hexToRgb(color);
  
  root.style.setProperty('--accent-color', color);
  
  if (rgb) {
    root.style.setProperty('--accent-r', String(rgb.r));
    root.style.setProperty('--accent-g', String(rgb.g));
    root.style.setProperty('--accent-b', String(rgb.b));
  }
};

export const AccentColorProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [accentColor, setAccentColorState] = useState<string>('#FFA845');

  // Load accent color on mount and set CSS variables
  useEffect(() => {
    // Set default CSS variables immediately
    updateCssVariables('#FFA845');
    
    GetAccentColor().then((color: string) => {
      if (color) {
        setAccentColorState(color);
        updateCssVariables(color);
      }
    }).catch(console.error);
  }, []);

  const setAccentColor = useCallback(async (color: string) => {
    setAccentColorState(color);
    updateCssVariables(color);
    try {
      await SetAccentColor(color);
    } catch (err) {
      console.error('Failed to save accent color:', err);
    }
  }, []);

  return (
    <AccentColorContext.Provider value={{ accentColor, setAccentColor }}>
      {children}
    </AccentColorContext.Provider>
  );
};

export const useAccentColor = (): AccentColorContextType => {
  const context = useContext(AccentColorContext);
  if (context === undefined) {
    throw new Error('useAccentColor must be used within an AccentColorProvider');
  }
  return context;
};

export default AccentColorContext;
